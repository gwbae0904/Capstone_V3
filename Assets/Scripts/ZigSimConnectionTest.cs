using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class ZigSimConnectionTest : MonoBehaviour
{
    [Header("연결할 오브젝트")]
    public Transform playerBody; // Player
    public Transform playerHead; // VRCamera

    [Header("조작 설정")]
    public float moveSpeed = 2f;
    public float rotationSmoothness = 15f;

    [Header("회전 방향 반전 (폰을 돌렸을 때 시선이 반대로 돌면 체크)")]
    [Tooltip("위아래(Pitch) 방향 반전. 기본값 켜짐 = 기존 코드의 -pitch 동작 그대로")]
    public bool invertPitch = true;
    [Tooltip("좌우(Yaw) 방향 반전")]
    public bool invertYaw = false;
    [Tooltip("옆으로 기울이기(Roll) 방향 반전")]
    public bool invertRoll = true;

    [Header("감도 (1 = 폰이 돈 각도 그대로, 2 = 폰이 45도 돌면 화면은 90도)")]
    [Tooltip("위아래(Pitch) 감도")]
    [Range(0.1f, 5f)] public float pitchSensitivity = 1f;
    [Tooltip("좌우(Yaw) 감도")]
    [Range(0.1f, 5f)] public float yawSensitivity = 1f;
    [Tooltip("옆으로 기울이기(Roll) 감도")]
    [Range(0.1f, 5f)] public float rollSensitivity = 1f;

    private UdpClient receiver;

    private readonly ConcurrentQueue<float[]> rotations =
        new ConcurrentQueue<float[]>();

    private readonly ConcurrentQueue<float[]> touches =
        new ConcurrentQueue<float[]>();

    private Quaternion initialCameraRotation;
    private Quaternion phoneStart;
    private Quaternion latestPhone;
    private Quaternion targetRotation;
    private bool calibrated;

    [Header("디버그")]
    [Tooltip("켜두면 화면 왼쪽 아래에 수신 상태(패킷 수, 보낸 IP 등)를 표시합니다. 시연 전엔 꺼두세요.")]
    public bool showDebugInfo = true;

    // 백그라운드 스레드에서 갱신, 메인 스레드(OnGUI)에서 읽음
    private int packetCount;
    private int quaternionCount;
    private int touchCount;
    private volatile string lastSenderInfo = "-";
    private volatile string lastError = "-";

    private Vector2 firstTouch;
    private Vector2 moveInput;
    private bool touching;
    private float lastTouchTime;

    void Start()
    {
        if (playerBody == null)
            playerBody = transform;

        if (playerHead == null && Camera.main != null)
            playerHead = Camera.main.transform;

        if (playerHead == null)
        {
            Debug.LogError("Player Head에 VRCamera를 연결해주세요.");
            enabled = false;
            return;
        }

        initialCameraRotation = playerHead.localRotation;
        targetRotation = initialCameraRotation;

        receiver = new UdpClient(9001);
        Task.Run(ReceiveLoop);

        Debug.Log("ZIG SIM 제어 시작: UDP 9001");
    }

    void ReceiveLoop()
    {
        while (receiver != null)
        {
            try
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] packet = receiver.Receive(ref sender);

                System.Threading.Interlocked.Increment(ref packetCount);
                lastSenderInfo = sender.Address + ":" + sender.Port;

                ReadQuaternion(packet);

                // 실제 수신 형식: touch01 = X, touch02 = Y
                float? x = ReadSingleFloat(
                    packet, "/ZIGSIM/phone/touch01");

                float? y = ReadSingleFloat(
                    packet, "/ZIGSIM/phone/touch02");

                if (x.HasValue && y.HasValue)
                {
                    touches.Enqueue(new float[] { x.Value, y.Value });
                    System.Threading.Interlocked.Increment(ref touchCount);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception e)
            {
                // 예전에는 여기서 break 해서, 패킷 하나 파싱이 실패하면 수신 루프가
                // 조용히 영구 종료됐음. 이제는 원인을 기록하고 다음 패킷을 계속 받음.
                lastError = e.GetType().Name + ": " + e.Message;
            }
        }
    }

    void ReadQuaternion(byte[] packet)
    {
        const string address = "/ZIGSIM/phone/quaternion";
        int start = FindAddress(packet, address);
        if (start < 0) return;

        int types = start + PaddedLength(address.Length + 1);
        int values = types + PaddedLength(6); // ",ffff"와 널 문자

        if (values + 16 > packet.Length ||
            packet[types] != (byte)',' ||
            packet[types + 1] != (byte)'f' ||
            packet[types + 2] != (byte)'f' ||
            packet[types + 3] != (byte)'f' ||
            packet[types + 4] != (byte)'f')
            return;

        rotations.Enqueue(new float[]
        {
            ReadFloat(packet, values),
            ReadFloat(packet, values + 4),
            ReadFloat(packet, values + 8),
            ReadFloat(packet, values + 12)
        });
        System.Threading.Interlocked.Increment(ref quaternionCount);
    }

    static float? ReadSingleFloat(byte[] packet, string address)
    {
        int start = FindAddress(packet, address);
        if (start < 0) return null;

        int types = start + PaddedLength(address.Length + 1);
        int values = types + 4; // ",f"와 널 문자

        if (values + 4 > packet.Length ||
            packet[types] != (byte)',' ||
            packet[types + 1] != (byte)'f')
            return null;

        return ReadFloat(packet, values);
    }

    static int FindAddress(byte[] packet, string address)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(address);

        for (int i = 0; i <= packet.Length - pattern.Length - 1; i++)
        {
            bool match = true;

            for (int j = 0; j < pattern.Length; j++)
            {
                if (packet[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match && packet[i + pattern.Length] == 0)
                return i;
        }

        return -1;
    }

    static int PaddedLength(int length)
    {
        return (length + 3) & ~3;
    }

    static float ReadFloat(byte[] data, int offset)
    {
        byte[] bytes = new byte[4];
        Array.Copy(data, offset, bytes, 0, 4);

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return BitConverter.ToSingle(bytes, 0);
    }

    void Update()
    {
        // 휴대폰 회전값 처리
        while (rotations.TryDequeue(out float[] q))
        {
            latestPhone = new Quaternion(q[0], -q[2], q[1], q[3]);

            if (!calibrated)
            {
                phoneStart = latestPhone;
                calibrated = true;
            }
        }

        // R: 현재 휴대폰 방향을 새로운 정면으로 설정
        if (Input.GetKeyDown(KeyCode.R) && calibrated)
        {
            phoneStart = latestPhone;
            Debug.Log("시선 초기화 완료");
        }

        if (calibrated)
        {
            Quaternion difference =
                Quaternion.Inverse(phoneStart) * latestPhone;

            Vector3 angles = difference.eulerAngles;
            float pitch = Mathf.DeltaAngle(0f, angles.x);
            float yaw = Mathf.DeltaAngle(0f, angles.y);
            float roll = Mathf.DeltaAngle(0f, angles.z);

            // 축별 방향 반전은 Inspector의 Invert Pitch/Yaw/Roll로 조절
            // 감도는 반전 전에 각도에 곱함 (폰이 돈 각도 x 감도 = 화면이 돌 각도)
            pitch *= pitchSensitivity;
            yaw *= yawSensitivity;
            roll *= rollSensitivity;

            targetRotation = initialCameraRotation * Quaternion.Euler(
                invertPitch ? -pitch : pitch,
                invertYaw ? -yaw : yaw,
                invertRoll ? -roll : roll);
        }

        // 터치 좌표 처리
        while (touches.TryDequeue(out float[] xy))
        {
            Vector2 currentTouch = new Vector2(xy[0], xy[1]);
            lastTouchTime = Time.time;

            if (!touching)
            {
                firstTouch = currentTouch;
                touching = true;
                Debug.Log("ZIG SIM 터치 입력 확인");
            }

            Vector2 delta = currentTouch - firstTouch;

            // 정규화 좌표 또는 픽셀 좌표에 맞춰 이동량 조절
            float range =
                Mathf.Abs(currentTouch.x) <= 1f &&
                Mathf.Abs(currentTouch.y) <= 1f
                    ? 0.4f
                    : 400f;

            moveInput = Vector2.ClampMagnitude(delta / range, 1f);

            if (moveInput.magnitude < 0.1f)
                moveInput = Vector2.zero;
        }

        // 손을 떼고 터치값이 끊기면 정지
        if (Time.time - lastTouchTime > 0.3f)
        {
            touching = false;
            moveInput = Vector2.zero;
        }

        // 바라보는 방향을 기준으로 앞뒤/좌우 이동
        if (playerBody != null &&
            playerHead != null &&
            moveInput.sqrMagnitude > 0f)
        {
            Vector3 forward = playerHead.forward;
            Vector3 right = playerHead.right;

            right.y = 0f;

            forward.Normalize();
            right.Normalize();

            Vector3 direction =
                - forward * moveInput.y + right * moveInput.x;

            direction = Vector3.ClampMagnitude(direction, 1f);

            Vector3 nextPosition =
                playerBody.position + direction * moveSpeed * Time.deltaTime;

            // 현재 카메라가 Player보다 얼마나 높은지 계산
            float cameraOffsetY =
                playerHead.position.y - playerBody.position.y;

            // 카메라 높이가 바닥(Y=0)에서 0.2m 이상이 되도록 제한
            nextPosition.y = Mathf.Max(
                nextPosition.y,
                0.2f - cameraOffsetY
            );

            playerBody.position = nextPosition;
        }
    }

    void LateUpdate()
    {
        if (calibrated && playerHead != null)
        {
            playerHead.localRotation = Quaternion.Slerp(
                playerHead.localRotation,
                targetRotation,
                Mathf.Clamp01(Time.deltaTime * rotationSmoothness)
            );
        }
    }

    void OnGUI()
    {
        if (!showDebugInfo) return;

        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = Color.black;
        style.fontStyle = FontStyle.Bold;
        style.fontSize = 12;

        float y = Screen.height - 110f;
        GUI.Label(new Rect(10, y, 700, 22), $"[ZIG SIM] 수신 패킷: {packetCount}  (보낸 곳: {lastSenderInfo})", style);
        GUI.Label(new Rect(10, y + 20, 700, 22), $"[ZIG SIM] 회전 인식: {quaternionCount}  터치 인식: {touchCount}  calibrated: {calibrated}", style);
        GUI.Label(new Rect(10, y + 40, 700, 22), $"[ZIG SIM] 마지막 오류: {lastError}", style);
    }

    void OnDestroy()
    {
        receiver?.Close();
        receiver = null;
    }
}