using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using WebSocketSharp;

public class P2PClient : INetEventListener
{
    private WebSocket _signaling;
    private RTCPeerConnection _peerConnection;
    private NetManager _netManager;
    private NetPeer _remotePeer;
    private List<RTCIceCandidate> _iceCandidates = new();
    private List<RTCIceCandidate> _remoteIceCandidates = new();
    private bool _readyDescription = false;
    private string _clientId;

    private readonly RTCIceServer[] _iceServers = new[]
    {
        new RTCIceServer {
             urls = new[] { "stun:stun.l.google.com:19302" }
        }
    };

    public P2PClient(string serverUrl, string clientId)
    {
        _clientId = clientId;
        _signaling = new WebSocket(serverUrl);
        SetupSignaling();
        SetupNetManager();
    }

    private void SetupSignaling()
    {
        _signaling.OnMessage += OnSignalingMessage;
        _signaling.OnOpen += (sender, e) =>
        {
            Logger.Instance.Log("시그널링 서버에 연결됨");
            Register();
        };
        _signaling.OnError += (sender, e) =>
        {
            Logger.Instance.Log($"시그널링 오류: {e.Message}");
        };
    }

    private void SetupNetManager()
    {
        _netManager = new NetManager(this);
        _netManager.Start();
    }

    public void Connect()
    {
        _signaling.Connect();
    }

    public IEnumerator CallPeerAsync(string peerId)
    {
        SetupPeerConnection();

        var offerOp = _peerConnection.CreateOffer();
        yield return offerOp;
        var offerDesc = offerOp.Desc;
        _peerConnection.SetLocalDescription(ref offerDesc);

        SendSignalingMessage("offer", peerId, new
        {
            sdp = offerDesc.sdp,
            type = offerDesc.type.ToString().ToLower()
        });

        Logger.Instance.Log($"Calling {peerId}...");
    }

    private void SetupPeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = _iceServers
        };

        _peerConnection = new RTCPeerConnection(ref config);
        Logger.Instance.Log("PeerConnection 생성됨");

        // ICE candidate 이벤트 처리
        _peerConnection.OnIceCandidate += (candidate) =>
        {
            if (candidate != null)
            {
                Logger.Instance.Log($"Local ICE Candidate: {candidate.Candidate}");
                _iceCandidates.Add(candidate);

                // 원격 피어에게 ICE candidate 전송
                SendSignalingMessage("ice-candidate", null, new
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex
                });
            }
        };

        _peerConnection.OnIceConnectionChange += (state) =>
        {
            Logger.Instance.Log($"ICE Connection State: {state}");

            if (state == RTCIceConnectionState.Completed)
            {
                // ICE 연결 완료 후 UDP 홀 펀칭 시도
                StartUdpHolePunching();
            }
        };
    }

    private void OnSignalingMessage(object sender, MessageEventArgs e)
    {
        try
        {
            var message = JsonConvert.DeserializeObject<SignalingMessage>(e.Data);

            switch (message.type)
            {
                case "offer":
                    CoroutineRunner.Instance.Run(HandleOffer(message));
                    break;
                case "answer":
                    CoroutineRunner.Instance.Run(HandleAnswer(message));
                    break;
                case "ice-candidate":
                    HandleIceCandidate(message);
                    break;
                case "peer-list":
                    HandlePeerList(message);
                    break;
                case "udp-info":
                    HandleUdpInfo(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.LogError(ex.Message);
        }
    }

    private IEnumerator HandleOffer(SignalingMessage message)
    {
        SetupPeerConnection();

        _ = new Timer(_ => Logger.Instance.Log(_peerConnection.IceConnectionState.ToString()), null, 0, 1000);

        var offer = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = message.data.GetValue("sdp").Value<string>()
        };

        yield return _peerConnection.SetRemoteDescription(ref offer);

        var answerOp = _peerConnection.CreateAnswer();
        yield return answerOp;
        var answerDesc = answerOp.Desc;
        yield return _peerConnection.SetLocalDescription(ref answerDesc);

        if (!_readyDescription && _remoteIceCandidates.Count > 0)
        {
            foreach (var c in _remoteIceCandidates)
            {
                Logger.Instance.Log($"Remote ICE Candidate: {c.Candidate}");
                _peerConnection.AddIceCandidate(c);
            }
            _remoteIceCandidates.Clear();
        }

        _readyDescription = true;

        SendSignalingMessage("answer", message.from, new
        {
            sdp = answerDesc.sdp,
            type = answerDesc.type.ToString().ToLower()
        });
    }

    private IEnumerator HandleAnswer(SignalingMessage message)
    {
        var answer = new RTCSessionDescription
        {
            type = RTCSdpType.Answer,
            sdp = message.data.GetValue("sdp").Value<string>()
        };

        yield return _peerConnection.SetRemoteDescription(ref answer);

        if (!_readyDescription && _remoteIceCandidates.Count > 0)
        {
            foreach (var c in _remoteIceCandidates)
            {
                Logger.Instance.Log($"Remote ICE Candidate: {c.Candidate}");
                _peerConnection.AddIceCandidate(c);
            }
            _remoteIceCandidates.Clear();
        }

        _readyDescription = true;
    }

    private void HandleIceCandidate(SignalingMessage message)
    {
        var candidate = new RTCIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.data.GetValue("candidate").Value<string>(),
            sdpMid = message.data.GetValue("sdpMid").Value<string>() ?? "0",
            sdpMLineIndex = message.data.GetValue("sdpMLineIndex").Value<int>()
        });

        if (!_readyDescription)
        {
            _remoteIceCandidates.Add(candidate);
            Logger.Instance.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");
        }
        else
        {
            _peerConnection.AddIceCandidate(candidate);

            Logger.Instance.Log($"Remote ICE Candidate: {candidate.Candidate}");
        }
    }

    private void HandlePeerList(SignalingMessage message)
    {
        var peers = message.data.GetValue("peers").Values();
        Logger.Instance.Log("사용 가능한 피어:");
        foreach (var peer in peers)
        {
            Logger.Instance.Log($"- {peer.Value<string>()}");
        }
    }

    private void HandleUdpInfo(SignalingMessage message)
    {
        var endpoint = message.data.GetValue("endpoint").Value<string>();
        var parts = endpoint.Split(':');
        var ip = parts[0];
        var port = int.Parse(parts[1]);

        // UDP 연결 시도
        _netManager.Connect(ip, port, "p2p-connection");
        Logger.Instance.Log($"UDP 연결 시도: {endpoint}");
    }

    private void StartUdpHolePunching()
    {
        // ICE를 통해 얻은 후보자 중에서 UDP 엔드포인트 추출
        foreach (var candidate in _iceCandidates)
        {
            if (candidate.Candidate.Contains("udp"))
            {
                // candidate에서 IP와 포트 추출
                var parts = candidate.Candidate.Split(' ');
                if (parts.Length > 4)
                {
                    var ip = parts[4];
                    if (int.TryParse(parts[5], out int port))
                    {
                        // 상대방에게 UDP 정보 전송
                        SendSignalingMessage("udp-info", null, new
                        {
                            endpoint = $"{ip}:{port}"
                        });
                        break;
                    }
                }
            }
        }
    }

    private void SendSignalingMessage(string type, string to, object data)
    {
        var message = new SignalingMessage
        {
            type = type,
            from = _clientId,
            to = to,
            data = JObject.FromObject(data)
        };

        _signaling.Send(JsonConvert.SerializeObject(message));
    }

    private void Register()
    {
        SendSignalingMessage("register", null, new { id = _clientId });
    }

    // LiteNetLib 이벤트 처리
    public void OnPeerConnected(NetPeer peer)
    {
        Logger.Instance.Log($"UDP 피어 연결됨: {peer}");
        _remotePeer = peer;

        // 테스트 메시지 전송
        SendUdpMessage("Hello P2P!");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Logger.Instance.Log($"UDP 피어 연결 해제: {peer.Address}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        var message = reader.GetString();
        Logger.Instance.Log($"UDP 메시지 수신: {message}");
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Logger.Instance.Log($"UDP 네트워크 에러: {socketError} at {endPoint}");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // 홀 펀칭을 위한 처리
        if (messageType == UnconnectedMessageType.BasicMessage)
        {
            var message = reader.GetString();
            if (message == "hole-punch")
            {
                // 홀 펀칭 응답
                var writer = new NetDataWriter();
                writer.Put("hole-punch-response");
                _netManager.SendUnconnectedMessage(writer, remoteEndPoint);
            }
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        Logger.Instance.Log($"지연시간 업데이트: {latency}ms");
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        request.Accept();
    }

    public void SendUdpMessage(string message)
    {
        if (_remotePeer != null)
        {
            var writer = new NetDataWriter();
            writer.Put(message);
            _remotePeer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    public void Update()
    {
        _netManager.PollEvents();
    }

    public void Dispose()
    {
        _netManager?.Stop();
        _peerConnection?.Close();
        _signaling?.Close();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
    {

    }
}

public class SignalingMessage
{
    public string type { get; set; }
    public string from { get; set; }
    public string to { get; set; }
    public JObject data { get; set; }
}

public class NetClient : MonoBehaviour
{
    [SerializeField]
    TMP_InputField id;
    [SerializeField]
    TMP_InputField cmd;

    private P2PClient client;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
       
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void Connect()
    {
        var clientId = id.text;

        client = new P2PClient("ws://localhost:8080", clientId);

        client.Connect();

        var updateTimer = new Timer(_ => client.Update(), null, 0, 15);
    }

    public void OnClick()
    {
        var parts = cmd.text.Split(' ', 2);
        var command = parts[0].ToLower();

        switch (command)
        {
            case "call":
                if (parts.Length > 1)
                {
                    StartCoroutine(client.CallPeerAsync(parts[1]));
                }
                break;
            case "send":
                if (parts.Length > 1)
                {
                    client.SendUdpMessage(parts[1]);
                }
                break;
        }
    }

    void OnDestroy()
    {
        client?.Dispose();   
    }
}