// SerialGloveReceiver.cs
using System;
using System.IO.Ports;
using UnityEngine;
using Valve.VR.InteractionSystem;

public class SerialGloveReceiver : MonoBehaviour
{
    [Header("시리얼 포트 설정")]
    [Tooltip("장치관리자에서 확인한 COM 포트 이름 (예: COM5)")]
    public string portName = "COM3"; // 기본값일 뿐, Inspector에서 자유롭게 바꿔서 씀
    public int baudRate = 115200;

    [Header("적용 대상")]
    [Tooltip("curl 평균값을 grabCurl에 자동으로 넣어줄 Hand (비워둬도 됨). hover/grab 판정 및 햅틱 로직에도 이 Hand를 사용합니다.")]
    public Hand targetHand;
    [Tooltip("IMU 회전을 적용할 Transform (보통 RightHand). ArucoHandTracker의 Apply Rotation은 꺼두세요 (서로 충돌 방지)")]
    public Transform targetTransform;
    [Tooltip("이 IMU 회전을 실제로 targetTransform에 적용할지 여부. 끄면 값은 계속 읽어오지만 적용은 안 함")]
    public bool applyRotation = true;

    [Header("MPU6050 축 보정 (실험적으로 맞추는 값)")]
    public bool invertX = false;
    public bool invertY = false;
    public bool invertZ = false;
    public AxisMapping axisMapping = AxisMapping.XYZ;

    public enum AxisMapping { XYZ, XZY, YXZ, YZX, ZXY, ZYX }

    [Header("햅틱 (손가락별 모터 정지 각도)")]
    [Tooltip("hover/grab 중인 물체에서 GraspPoseTrigger를 못 찾았을 때 쓸 기본 정지 각도 (0~180도, 5손가락 모두 동일하게 적용됨)")]
    [Range(0, 180)]
    public int defaultHapticStopAngle = 90;

    // 아두이노는 0~1000 값을 받으므로, 위 각도를 GraspPoseTrigger와 동일한 공식으로 변환
    private int DefaultAngleToWireValue()
    {
        return Mathf.RoundToInt((180 - defaultHapticStopAngle) / 180f * 1000f);
    }

    [Header("디버그 (읽기 전용)")]
    public float[] curls = new float[5];
    public Quaternion currentRotation = Quaternion.identity;
    public bool isConnected = false;

    private bool isHandClosed = false;

    private SerialPort serialPort;
    private string leftoverBuffer = "";
    private byte[] readByteBuffer = new byte[4096];
    private float tareMessageTimer = 0f;

    void Start()
    {
        TryOpenPort();
    }

    private void TryOpenPort()
    {
        try
        {
            serialPort = new SerialPort(portName, baudRate);
            serialPort.ReadTimeout = 50;
            serialPort.NewLine = "\n";
            serialPort.Open();
            isConnected = true;

            // 연결되는 순간 모터를 전부 0(완전히 풀림)으로 리셋.
            // 아두이노 자체는 부팅 시 0으로 초기화하지만, 아두이노는 계속 켜진 채로
            // Unity만 재시작한 경우 이전 세션에서 마지막으로 보낸 값이 서보에 그대로
            // 남아있을 수 있어서, 연결 시점에 한 번 더 확실히 리셋함.
            SendHapticCommand(new int[] { 0, 0, 0, 0, 0 });
            isHandClosed = false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SerialGloveReceiver] {portName} 포트를 열지 못했습니다: {e.Message}");
            isConnected = false;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T))
        {
            SendTareCommand();
            tareMessageTimer = 2f;
            Debug.Log("[SerialGloveReceiver] T키 입력 -> IMU 영점 재조절(tare) 명령 전송");
        }
        if (tareMessageTimer > 0f) tareMessageTimer -= Time.deltaTime;

        if (!isConnected || serialPort == null || !serialPort.IsOpen)
            return;

        try
        {
            int bytesToRead = serialPort.BytesToRead;
            if (bytesToRead > 0)
            {
                int count = Math.Min(bytesToRead, readByteBuffer.Length);
                int bytesRead = serialPort.Read(readByteBuffer, 0, count);
                if (bytesRead > 0)
                {
                    string incoming = System.Text.Encoding.ASCII.GetString(readByteBuffer, 0, bytesRead);

                    leftoverBuffer += incoming;
                    string[] lines = leftoverBuffer.Split('\n');
                    leftoverBuffer = lines[lines.Length - 1];

                    for (int i = lines.Length - 2; i >= 0; i--)
                    {
                        if (TryParseLine(lines[i].Trim()))
                            break;
                    }
                }
            }
        }
        catch (TimeoutException)
        {
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SerialGloveReceiver] 읽기 오류: {e.Message}");
        }

        UpdateHaptics();
    }

    // 물체 크기(=손가락이 어디까지 굽혀지도록 허용할지)에 맞춰 모터를 미리/계속 세팅.
    // - hover만 하는 중: 그 물체의 정지값으로 미리 세팅 (실제로 잡기 전에 준비)
    // - 실제로 잡은 중: 같은 값 유지 (진짜 저항은 기계적 스토퍼가 만들어줌)
    // - 아무 물체도 근처에 없음: 0으로 완전히 풀기
    private void UpdateHaptics()
    {
        bool isGrabbingObject = targetHand != null && targetHand.currentAttachedObject != null;
        bool isHoveringObject = targetHand != null && targetHand.hoveringInteractable != null;

        if (isGrabbingObject || isHoveringObject)
        {
            GameObject relevantObject = isGrabbingObject
                ? targetHand.currentAttachedObject
                : targetHand.hoveringInteractable.gameObject;

            GraspPoseTrigger trigger = relevantObject.GetComponentInChildren<GraspPoseTrigger>();
            if (trigger == null) trigger = relevantObject.GetComponentInParent<GraspPoseTrigger>();

            int[] hapticValues;
            if (trigger != null)
            {
                hapticValues = trigger.GetHapticStopValues();
            }
            else
            {
                int v = DefaultAngleToWireValue();
                hapticValues = new int[] { v, v, v, v, v };
            }

            SendHapticCommand(hapticValues);
            isHandClosed = true;
        }
        else if (isHandClosed)
        {
            // hover도 grab도 아닌 상태로 돌아오면 완전히 풀기
            isHandClosed = false;
            SendHapticCommand(new int[] { 0, 0, 0, 0, 0 });
        }
    }

    private bool TryParseLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        string[] parts = line.Split(',');
        if (parts.Length != 9) return false;

        try
        {
            for (int i = 0; i < 5; i++)
                curls[i] = float.Parse(parts[i]);

            float qw = float.Parse(parts[5]);
            float qx = float.Parse(parts[6]);
            float qy = float.Parse(parts[7]);
            float qz = float.Parse(parts[8]);

            currentRotation = ConvertImuQuaternion(qw, qx, qy, qz);

            if (targetHand != null)
            {
                float avgCurl = 0f;
                for (int i = 0; i < 5; i++) avgCurl += curls[i];
                targetHand.grabCurl = avgCurl / 5f;
            }

            if (targetTransform != null && applyRotation)
                targetTransform.localRotation = currentRotation;

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private Quaternion ConvertImuQuaternion(float w, float x, float y, float z)
    {
        float ax, ay, az;
        switch (axisMapping)
        {
            case AxisMapping.XZY: ax = x; ay = z; az = y; break;
            case AxisMapping.YXZ: ax = y; ay = x; az = z; break;
            case AxisMapping.YZX: ax = y; ay = z; az = x; break;
            case AxisMapping.ZXY: ax = z; ay = x; az = y; break;
            case AxisMapping.ZYX: ax = z; ay = y; az = x; break;
            default: ax = x; ay = y; az = z; break;
        }

        if (invertX) ax = -ax;
        if (invertY) ay = -ay;
        if (invertZ) az = -az;

        return new Quaternion(ax, ay, az, w);
    }

    public void SendTareCommand()
    {
        SendLine("t");
    }

    public void SendResetFingerCalibration()
    {
        SendLine("r");
    }

    public void SendHapticCommand(int[] limits)
    {
        if (limits == null || limits.Length != 5) return;
        SendLine($"H,{limits[0]},{limits[1]},{limits[2]},{limits[3]},{limits[4]}");
    }

    private void SendLine(string command)
    {
        if (!isConnected || serialPort == null || !serialPort.IsOpen) return;
        try
        {
            serialPort.WriteLine(command);
        }
        catch (Exception) { }
    }

    void OnDestroy()
    {
        if (serialPort != null && serialPort.IsOpen)
            serialPort.Close();
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = Color.black;
        style.fontStyle = FontStyle.Bold;
        style.fontSize = 12;
        style.alignment = TextAnchor.UpperRight;

        int rectWidth = 400;
        int startX = Screen.width - rectWidth - 10;

        int startY = 10;
        int spacing = 20;

        GUI.Label(new Rect(startX, startY, rectWidth, 24), $"Serial connected: {isConnected}  (T키: IMU 영점 재조절)", style);

        if (isConnected)
        {
            startY += spacing + 5;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), "[ 실시간 가변저항 굽힘도 (0.0=쫙 폄, 1.0=꽉 쥠) ]", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"엄지 (Thumb) : {curls[0]:F2}", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"검지 (Index) : {curls[1]:F2}", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"중지 (Middle) : {curls[2]:F2}", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"약지 (Ring)   : {curls[3]:F2}", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"소지 (Pinky)  : {curls[4]:F2}", style);

            startY += spacing + 10;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"IMU Rotation (euler): {currentRotation.eulerAngles}", style);
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), $"Hand Closed State: {isHandClosed}", style);

            if (targetHand != null)
            {
                startY += spacing;
                string hoverName = targetHand.hoveringInteractable != null ? targetHand.hoveringInteractable.name : "-";
                string grabName = targetHand.currentAttachedObject != null ? targetHand.currentAttachedObject.name : "-";
                GUI.Label(new Rect(startX, startY, rectWidth, 24), $"Hover: {hoverName}  Grab: {grabName}", style);
            }
        }

        if (tareMessageTimer > 0f)
        {
            startY += spacing;
            GUI.Label(new Rect(startX, startY, rectWidth, 24), "-> IMU 영점 재조절(tare) 명령 전송함", style);
        }
    }
}
