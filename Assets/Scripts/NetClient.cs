using System.Threading;
using TMPro;
using UnityEngine;

public class NetClient : MonoBehaviour
{
    [SerializeField]
    TMP_InputField id;
    [SerializeField]
    TMP_InputField cmd;

    [SerializeField]
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

        client.Init("ws://localhost:8080", clientId);

        client.Connect();

        //var updateTimer = new Timer(_ => client.Update(), null, 0, 15);
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