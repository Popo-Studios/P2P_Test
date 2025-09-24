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
using UnityEngine.PlayerLoop;
using WebSocketSharp;
using System.Collections.Concurrent;

public class P2PClient : MonoBehaviour, INetEventListener
{
    private WebSocket _signaling;
    private RTCPeerConnection _peerConnection;
    private NetManager _netManager;
    private NetPeer _remotePeer;
    private List<RTCIceCandidate> _iceCandidates = new();
    private List<RTCIceCandidate> _remoteIceCandidates = new();
    private bool _readyDescription = false;
    private string _clientId;

    // 스레드 안전한 메시지 큐들
    private readonly ConcurrentQueue<string> _incomingMessages = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _errorMessages = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _logMessages = new ConcurrentQueue<string>();
    
    // WebRTC 콜백들을 위한 큐
    private readonly ConcurrentQueue<RTCIceCandidate> _iceCandidateQueue = new ConcurrentQueue<RTCIceCandidate>();
    private readonly ConcurrentQueue<RTCIceConnectionState> _connectionStateQueue = new ConcurrentQueue<RTCIceConnectionState>();

    // 연결 상태 추적
    private bool _isConnected = false;

    private readonly RTCIceServer[] _iceServers = new[]
    {
        new RTCIceServer {
             urls = new[] { "stun:stun.l.google.com:19302" }
        }
    };

    public void Init(string serverUrl, string clientId)
    {
        _clientId = clientId;
        _signaling = new WebSocket(serverUrl);

        SetupSignaling();
        SetupNetManager();
    }

    private void SetupSignaling()
    {
        // 모든 WebSocket 이벤트를 큐에 저장만 하고, Update에서 처리
        _signaling.OnMessage += (sender, e) => {
            _incomingMessages.Enqueue(e.Data);
        };

        _signaling.OnOpen += (sender, e) => {
            _logMessages.Enqueue("시그널링 서버에 연결됨");
            _isConnected = true;
            // Register 메시지도 큐에 넣어서 메인 스레드에서 처리
            Register();
        };

        _signaling.OnError += (sender, e) => {
            _errorMessages.Enqueue($"시그널링 오류: {e.Message}");
        };

        _signaling.OnClose += (sender, e) => {
            _logMessages.Enqueue($"시그널링 연결 종료: {e.Reason}");
            _isConnected = false;
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

        Debug.Log($"Calling {peerId}...");
    }

    private void SetupPeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = _iceServers
        };

        _peerConnection = new RTCPeerConnection(ref config);
        var channel = _peerConnection.CreateDataChannel("channel");
        
        Debug.Log("PeerConnection 생성됨");

        // ICE candidate 이벤트를 큐에 저장
        _peerConnection.OnIceCandidate += (candidate) =>
        {
            if (candidate != null)
            {
                _iceCandidateQueue.Enqueue(candidate);
            }
        };

        // 연결 상태 변경을 큐에 저장
        _peerConnection.OnIceConnectionChange += (state) =>
        {
            _connectionStateQueue.Enqueue(state);
        };
    }

    private IEnumerator OnSignalingMessage(SignalingMessage message)
    {
        Debug.Log(message.type);
        switch (message.type)
        {
            case "offer":
                yield return HandleOffer(message);
                break;
            case "answer":
                yield return HandleAnswer(message);
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
        yield break;
    }

    private IEnumerator HandleOffer(SignalingMessage message)
    {
        SetupPeerConnection();

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

        ProcessQueuedIceCandidates();
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

        ProcessQueuedIceCandidates();
        _readyDescription = true;
    }

    private void ProcessQueuedIceCandidates()
    {
        if (!_readyDescription && _remoteIceCandidates.Count > 0)
        {
            foreach (var c in _remoteIceCandidates)
            {
                Debug.Log($"Processing Queued Remote ICE Candidate: {c.Candidate}");
                _peerConnection.AddIceCandidate(c);
            }
            _remoteIceCandidates.Clear();
        }
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
            Debug.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");
        }
        else
        {
            _peerConnection.AddIceCandidate(candidate);
            Debug.Log($"Added Remote ICE Candidate: {candidate.Candidate}");
        }
    }

    private void HandlePeerList(SignalingMessage message)
    {
        var peers = message.data.GetValue("peers").Values();
        Debug.Log("사용 가능한 피어:");
        foreach (var peer in peers)
        {
            Debug.Log($"- {peer.Value<string>()}");
        }
    }

    private void HandleUdpInfo(SignalingMessage message)
    {
        var endpoint = message.data.GetValue("endpoint").Value<string>();
        var parts = endpoint.Split(':');
        var ip = parts[0];
        var port = int.Parse(parts[1]);

        _netManager.Connect(ip, port, "p2p-connection");
        Debug.Log($"UDP 연결 시도: {endpoint}");
    }

    private void StartUdpHolePunching()
    {
        foreach (var candidate in _iceCandidates)
        {
            if (candidate.Candidate.Contains("udp"))
            {
                var parts = candidate.Candidate.Split(' ');
                if (parts.Length > 4)
                {
                    var ip = parts[4];
                    if (int.TryParse(parts[5], out int port))
                    {
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
        if (!_isConnected) return;

        try
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
        catch (Exception ex)
        {
            Debug.LogError($"Failed to send signaling message: {ex.Message}");
        }
    }

    private void Register()
    {
        SendSignalingMessage("register", null, new { id = _clientId });
    }

    // LiteNetLib 이벤트 처리
    public void OnPeerConnected(NetPeer peer)
    {
        Debug.Log($"UDP 피어 연결됨: {peer}");
        _remotePeer = peer;
        SendUdpMessage("Hello P2P!");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Debug.Log($"UDP 피어 연결 해제: {peer.Address}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        var message = reader.GetString();
        Debug.Log($"UDP 메시지 수신: {message}");
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Debug.LogError($"UDP 네트워크 에러: {socketError} at {endPoint}");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (messageType == UnconnectedMessageType.BasicMessage)
        {
            var message = reader.GetString();
            if (message == "hole-punch")
            {
                var writer = new NetDataWriter();
                writer.Put("hole-punch-response");
                _netManager.SendUnconnectedMessage(writer, remoteEndPoint);
            }
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        Debug.Log($"지연시간 업데이트: {latency}ms");
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

    void Update()
    {
        // LiteNetLib 이벤트 처리
        _netManager?.PollEvents();

        // 로그 메시지 처리
        while (_logMessages.TryDequeue(out var logMsg))
        {
            Debug.Log(logMsg);
        }

        // 에러 메시지 처리
        while (_errorMessages.TryDequeue(out var errorMsg))
        {
            Debug.LogError(errorMsg);
        }

        // ICE candidate 처리
        while (_iceCandidateQueue.TryDequeue(out var candidate))
        {
            Debug.Log($"Local ICE Candidate: {candidate.Candidate}");
            _iceCandidates.Add(candidate);

            SendSignalingMessage("ice-candidate", null, new
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex
            });
        }

        // 연결 상태 변경 처리
        while (_connectionStateQueue.TryDequeue(out var state))
        {
            Debug.Log($"ICE Connection State: {state}");

            if (state == RTCIceConnectionState.Completed)
            {
                StartUdpHolePunching();
            }
        }

        // WebSocket 메시지 처리
        while (_incomingMessages.TryDequeue(out var messageData))
        {
            try
            {
                var message = JsonConvert.DeserializeObject<SignalingMessage>(messageData);
                Debug.Log(message.type);
                StartCoroutine(OnSignalingMessage(message));
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to process signaling message: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _netManager?.Stop();
        _peerConnection?.Close();
        _signaling?.Close();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        // 중복 메서드 - 필요에 따라 구현
    }
}

public class SignalingMessage
{
    public string type { get; set; }
    public string from { get; set; }
    public string to { get; set; }
    public JObject data { get; set; }
}