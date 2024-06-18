using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using UnityEngine.SceneManagement;
using System.Collections;
using UnityEngine.Networking;
using System.Threading.Tasks;
using UnityEditor;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Unity.VisualScripting.Dependencies.NCalc;
using System.Text.RegularExpressions;
using Unity.VisualScripting;

public class WebSocketManager : MonoBehaviour
{
    public WebSocket ws;
    public int executionsPerSecond = 20;
    private float interval;
    private float timer;
    private string webSocket_address;
    private string imageServer_address;
    private bool connected = false;

    public GameObject player;
    public Camera vThirdPersonCamera;
    public GameObject otherPlayerPrefab;

    // UI
    public Canvas controlPromptsUI;
    public Canvas menuUI;
    public Button connectBtn;
    public Button[] roomBtns;
    public TMP_InputField addressInput;
    public TMP_InputField portInput;
    public TMP_InputField imageServerURL;
    public GameObject eventSystem;

    public TMP_InputField usernameInput;

    public GameObject imageViewer;
    public Button menuBtn;
    public Button menuCloseBtn;
    public CanvasGroup connectioInfo;
    public Button connectionInfoBtn;
    public Button connectionInfoCloseBtn;
    public TMP_Text statusDisplay;

    public RawImageClickHandler PerspectiveBtn;


    private Dictionary<string, GameObject> players = new Dictionary<string, GameObject>();
    private readonly Queue<Action> actions = new Queue<Action>();
    private readonly object lockObject = new object();

    private UdpClient udpClient;
    private IPEndPoint broadcastEndPoint;
    private const int BroadcastPort = 8382;


    void Start()
    {
        udpClient = new UdpClient
        {
            EnableBroadcast = true
        };
        broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, BroadcastPort);

        SendBroadcastMessage();
        ListenForUdpResponses();

        connectBtn.onClick.AddListener(OnConnectBtnClick);

        menuBtn.onClick.AddListener(() => OpenMenu());
        menuCloseBtn.onClick.AddListener(() => CloseMenu());

        connectionInfoBtn.onClick.AddListener(() =>
        {
            connectioInfo.alpha = 1;
            connectioInfo.interactable = true;
            connectioInfo.blocksRaycasts = true;
        });
        connectionInfoCloseBtn.onClick.AddListener(() =>
        {
            connectioInfo.alpha = 0;
            connectioInfo.interactable = false;
            connectioInfo.blocksRaycasts = false;
        });

        foreach (var roomBtn in roomBtns)
        {
            roomBtn.onClick.AddListener(() => OnRoomBtnClick(roomBtn.name));
        }

        SceneManager.sceneLoaded += OnSceneLoaded;

        DontDestroyOnLoad(gameObject);
        DontDestroyOnLoad(player);
        DontDestroyOnLoad(vThirdPersonCamera);
        DontDestroyOnLoad(controlPromptsUI);
        DontDestroyOnLoad(menuUI);
        DontDestroyOnLoad(eventSystem);

        interval = 1f / executionsPerSecond;
        timer = 0f;

    }

    void OpenMenu()
    {
        menuUI.enabled = true;
        usernameInput.interactable = true;
    }
    void CloseMenu()
    {
        usernameInput.interactable = false;
        menuUI.enabled = false;
    }
    private void SendBroadcastMessage()
    {
        string message = "Discover WebSocket Server";
        byte[] data = Encoding.UTF8.GetBytes(message);
        udpClient.Send(data, data.Length, broadcastEndPoint);
        Debug.Log("在區域網路中廣播UDP來尋找伺服器");
        statusDisplay.text = "正在尋找伺服器";
    }

    private async void ListenForUdpResponses()
    {
        System.Diagnostics.Stopwatch stopwatch = new();
        stopwatch.Start();

        Debug.Log("等待回應");
        while (true)
        {
            UdpReceiveResult result = await udpClient.ReceiveAsync();
            string receivedMessage = Encoding.UTF8.GetString(result.Buffer);
            Debug.Log($"收到來自 {result.RemoteEndPoint} 的 UDP 訊息 : {receivedMessage}");

            if (receivedMessage.Contains("WebSocket server is here"))
            {
                addressInput.text = result.RemoteEndPoint.Address.ToString();
                portInput.text = result.RemoteEndPoint.Port.ToString();
                Debug.Log($"已確定 {result.RemoteEndPoint} 為WebSocket伺服器位址");
                Debug.Log("不再等待回應");

                statusDisplay.text = "確定伺服器位址，開始連線";
                OnConnectBtnClick();
                break;
            }

            if (stopwatch.ElapsedMilliseconds > 10000)
            {
                statusDisplay.text = "失敗: 找不到伺服器";
                Console.WriteLine("UDP等待回應時間超過10秒");
                Debug.Log("不再等待回應");
                break;
            }
        }
    }


    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log("上傳使用者名稱: " + usernameInput.text);
        ws.Send("joinroom:" + scene.name + ":" + usernameInput.text);
    }

    void OnConnectBtnClick()
    {

        webSocket_address = "ws://" + addressInput.text + ":" + portInput.text;

        statusDisplay.text = "嘗試連線WebSocket";
        Debug.Log("嘗試連線WebSocket: " + webSocket_address);

        if (ws != null && ws.IsAlive)
        {
            ws.Close();
        }

        ws = new WebSocket(webSocket_address)
        {
            WaitTime = TimeSpan.FromMilliseconds(2000)
        };

        ws.OnMessage += OnMessage;
        ws.OnOpen += OnOpen;
        ws.OnClose += OnClose;
        ws.OnError += OnError;

        ws.Connect();
    }

    void OnRoomBtnClick(string room)
    {
        player.transform.position = Vector3.zero;
        SceneManager.LoadScene(room);
        CloseMenu();
    }


    void Update()
    {
        if (!connected) return;

        ExecuteActions();

        timer += Time.deltaTime;

        if (timer >= interval)
        {
            timer -= interval;
            WebSocketAction();
        }


        if (PerspectiveBtn.PressDown())
        {
            vThirdPersonCamera c = Camera.main.GetComponent<vThirdPersonCamera>();
            if (c.defaultDistance == 2.5f)
            {
                int layerMask = 1 << LayerMask.NameToLayer("Player");
                Camera.main.cullingMask &= ~(1 << layerMask);
                c.defaultDistance = 0f;
            }
            else
            {
                int layerMask = 1 << LayerMask.NameToLayer("Player");
                Camera.main.cullingMask |= (1 << layerMask);
                c.defaultDistance = 2.5f;
            }

        }
    }

    public void ExecuteInMainThread(Action action)
    {
        lock (lockObject)
        {
            actions.Enqueue(action);
        }

    }
    private void ExecuteActions()
    {
        lock (lockObject)
        {
            while (actions.Count > 0)
            {
                var action = actions.Dequeue();
                action.Invoke();
            }
        }
    }

    void WebSocketAction()
    {
        ws.Send("playerData:" + GameObjectSerializer.SerializeGameObject(player));
    }

    void OnMessage(object sender, MessageEventArgs e)
    {
        var messageData = JsonUtility.FromJson<MessageData>(e.Data);
        ExecuteInMainThread(() =>
        {
            switch (messageData.type)
            {
                case "playerData":

                    if (!players.ContainsKey(messageData.uuid))
                    {
                        // 創建新的玩家物件
                        GameObject newPlayer = Instantiate(otherPlayerPrefab);
                        players[messageData.uuid] = newPlayer;
                    }
                    // 更新玩家物件
                    GameObject player = players[messageData.uuid];
                    GameObjectSerializer.DeserializeGameObject(player, messageData.data);

                    break;
                case "disconnect":
                    if (players.ContainsKey(messageData.uuid))
                    {
                        // 刪除玩家物件
                        Destroy(players[messageData.uuid]);
                        players.Remove(messageData.uuid);
                    }
                    break;
                case "image":
                    Debug.Log("開始從ImageServer獲取圖片: " + messageData.imageName);

                    GameObject targetObject = GameObject.Find(messageData.imageName);
                    if (targetObject == null)
                    {
                        Debug.LogError("(image)找不到物件: " + messageData.imageName);
                        return;
                    }


                    targetObject.GetComponent<PlaneClickDetector>().UpdateImage(messageData.isLiked, messageData.likeCount, messageData.comments);

                    imageServer_address = imageServerURL.text;

                    if (imageServer_address == "")
                        imageServer_address = addressInput.text + ":" + portInput.text;

                    if (!imageServer_address.StartsWith("http://") && !imageServer_address.StartsWith("https://"))
                        imageServer_address = "http://" + imageServer_address;

                    if (!imageServer_address.EndsWith("/"))
                        imageServer_address += "/";

                    StartCoroutine(GetImageAndModify(targetObject, imageServer_address + messageData.imagePath));
                    // ModifyDrawingImage(targetObject, messageData.imageData);
                    break;
                case "image_update":
                    Debug.Log("準備更新圖片資料: " + messageData.imageName);
                    GameObject targetUpdateObject = GameObject.Find(messageData.imageName);

                    if (targetUpdateObject != null)
                        if (targetUpdateObject.GetComponent<PlaneClickDetector>() != null)
                            targetUpdateObject.GetComponent<PlaneClickDetector>().UpdateImage(messageData.isLiked, messageData.likeCount, messageData.comments);
                        else
                            targetUpdateObject.GetComponent<_3DObjectClickDetector>().Update3DObject(messageData.isLiked, messageData.likeCount, messageData.comments);
                    else
                        Debug.LogError("(image_update)找不到物件: " + messageData.imageName);

                    break;
            }
        });
    }

    IEnumerator GetImageAndModify(GameObject targetObject, string url)
    {
        Debug.Log("Downloading: " + url);

        UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError(request.error);
        }
        else
        {
            Texture2D texture = DownloadHandlerTexture.GetContent(request);

            ModifyDrawingImage(targetObject, texture);
        }
    }
    void OnOpen(object sender, EventArgs e)
    {
        statusDisplay.text = "連線成功";
        Debug.Log("WebSocket 連線成功: " + webSocket_address);
        connected = true;

    }

    void OnClose(object sender, CloseEventArgs e)
    {
        ExecuteInMainThread(() => menuUI.enabled = true);

        statusDisplay.text = "錯誤: 連線關閉";
        Debug.Log("WebSocket 連線關閉: " + e.Reason);

        connected = false;

    }

    void OnError(object sender, ErrorEventArgs e)
    {
        Debug.LogError("WebSocket 錯誤: " + e);
    }

    void ModifyDrawingImage(GameObject targetObject, Texture2D texture)
    {
        targetObject.GetComponent<PlaneClickDetector>().texture2D = texture;

        Renderer renderer = targetObject.GetComponent<Renderer>();

        if (renderer != null)
        {
            Material material = new Material(Shader.Find("Standard"))
            {
                mainTexture = texture
            };
            renderer.material = material;
        }
        else
        {
            Debug.LogError("Renderer not found on target object.");
        }

    }

    [System.Serializable]
    public class MessageData
    {
        public string type;
        public string uuid;
        public string data;
        public string imageName;
        public string imagePath;
        public bool isLiked;
        public int likeCount;
        public string comments;
    }
}
