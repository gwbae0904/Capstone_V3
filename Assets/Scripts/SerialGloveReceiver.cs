// SerialGloveReceiver.cs
//
// 아두이노(ESP32 + MPU6050 + 포텐셔미터 5개)로부터
// "c0,c1,c2,c3,c4,qw,qx,qy,qz\n" 형식의 데이터를 받아서
// - 손가락 curl 값 5개
// - 손의 회전(IMU 쿼터니언, 이미 tare 기준 상대값으로 옴)
// 을 Unity에 반영합니다.
//
// 양방향 통신도 지원합니다 (Unity -> 아두이노):
// - "t"  : IMU 영점 재조절(tare) 요청
// - "r"  : 손가락 min/max 캘리브레이션 초기화 요청
// - "H,v0,v1,v2,v3,v4" : 햅틱 서보 목표값 전송 (0~1000, 엄지/검지/중지/약지/소지 순서)

using System;
using System.IO.Ports;
using UnityEngine;
using Valve.VR.InteractionSystem;

public class SerialGloveReceiver : MonoBehaviour
{
    [Header("시리얼 포트 설정")]
    [Tooltip("장치관리자에서 확인한 COM 포트 이름 (예: COM5)")]
    public string portName = "COM5";
    public int baudRate = 115200;

    [Header("적용 대상")]
    [Tooltip("curl 평균값을 grabCurl에 자동으로 넣어줄 Hand (비워둬도 됨)")]
    public Hand targetHand;
    [Tooltip("IMU 회전을 적용할 Transform (보통 RightHand). ArucoHandTracker의 Apply Rotation은 꺼두세요 (서로 충돌 방지)")]
    public Transform targetTransform;
    [Tooltip("이 IMU 회전을 실제로 targetTransform에 적용할지 여부. 끄면 값은 계속 읽어오지만 적용은 안 함")]
    public bool applyRotation = true;

    [Header("MPU6050 축 보정 (실험적으로 맞추는 값)")]
    [Tooltip("MPU6050이 실제 글러브에 어떤 방향으로 붙어있는지에 따라 축이 안 맞을 수 있어요. 회전이 이상하면 이 값들을 하나씩 바꿔가며 테스트해보세요.")]
    public bool invertX = false;
    public bool invertY = false;
    public bool invertZ = false;
    [Tooltip("MPU6050 DMP의 축 배치 자체가 Unity(X우, Y상, Z전방)랑 다를 수 있어서, 어느 축을 어디에 매핑할지 선택. 기본값부터 테스트하고 안 맞으면 바꿔보세요.")]
    public AxisMapping axisMapping = AxisMapping.XYZ;

    public enum AxisMapping { XYZ, XZY, YXZ, YZX, ZXY, ZYX }

    [Header("디버그 (읽기 전용)")]
    public float[] curls = new float[5];
    public Quaternion currentRotation = Quaternion.identity;
    public bool isConnected = false;

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
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SerialGloveReceiver] {portName} 포트를 열지 못했습니다: {e.Message}");
            isConnected = false;
        }
    }

    void Update()
    {
        // T키를 누르면 아두이노에 IMU 영점 재조절(tare) 명령 전송
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
            if (bytesToRead <= 0) return;

            int count = Math.Min(bytesToRead, readByteBuffer.Length);
            int bytesRead = serialPort.Read(readByteBuffer, 0, count);
            if (bytesRead <= 0) return;

            string incoming = System.Text.Encoding.ASCII.GetString(readByteBuffer, 0, bytesRead);

            leftoverBuffer += incoming;
            string[] lines = leftoverBuffer.Split('\n');
            leftoverBuffer = lines[lines.Length - 1];

            // 가장 최근의 완성된 줄 하나만 파싱 (오래된 값은 버려서 지연 최소화)
            for (int i = lines.Length - 2; i >= 0; i--)
            {
                if (TryParseLine(lines[i].Trim()))
                    break;
            }
        }
        catch (TimeoutException)
        {
            // 이번 프레임에 새 데이터 없음, 무시
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SerialGloveReceiver] 읽기 오류: {e.Message}");
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
            return false; // 중간에 깨진 줄이 온 경우, 그냥 무시하고 다음 줄 시도
        }
    }

    // MPU6050 DMP의 쿼터니언 축 배치를 Unity 좌표계로 변환.
    // 실제 부착 방향에 따라 이 매핑이 안 맞을 수 있어서, Inspector의
    // axisMapping/invertX/Y/Z 값을 바꿔가며 실험적으로 맞추는 용도입니다.
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
            default: ax = x; ay = y; az = z; break; // XYZ
        }

        if (invertX) ax = -ax;
        if (invertY) ay = -ay;
        if (invertZ) az = -az;

        return new Quaternion(ax, ay, az, w);
    }

    // ===============================
    // Unity -> 아두이노 명령 전송
    // ===============================

    /// <summary>IMU 영점을 지금 이 순간 기준으로 다시 잡음 (아두이노의 't' 명령)</summary>
    public void SendTareCommand()
    {
        SendLine("t");
    }

    /// <summary>손가락 min/max 캘리브레이션을 초기화 (아두이노의 'r' 명령)</summary>
    public void SendResetFingerCalibration()
    {
        SendLine("r");
    }

    /// <summary>
    /// 손가락별 햅틱 강도 전송 (0~1000, 엄지/검지/중지/약지/소지 순서).
    /// 예: SendHapticCommand(new int[]{0, 800, 0, 0, 0});
    /// </summary>
    public void SendHapticCommand(int[] limits)
    {
        if (limits == null || limits.Length != 5)
        {
            Debug.LogWarning("[SerialGloveReceiver] SendHapticCommand는 길이 5인 배열이 필요합니다.");
            return;
        }
        SendLine($"H,{limits[0]},{limits[1]},{limits[2]},{limits[3]},{limits[4]}");
    }

    private void SendLine(string command)
    {
        if (!isConnected || serialPort == null || !serialPort.IsOpen)
        {
            Debug.LogWarning($"[SerialGloveReceiver] 포트가 연결되지 않아 명령을 보낼 수 없습니다: {command}");
            return;
        }
        try
        {
            serialPort.WriteLine(command);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SerialGloveReceiver] 명령 전송 실패: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (serialPort != null && serialPort.IsOpen)
            serialPort.Close();
    }

    void OnGUI()
    {
        GUI.Label(new Rect(10, 180, 400, 24), $"Serial connected: {isConnected}  (T키: IMU 영점 재조절)");
        if (isConnected)
        {
            GUI.Label(new Rect(10, 204, 500, 24), $"Curls: {string.Join(", ", Array.ConvertAll(curls, c => c.ToString("F2")))}");
            GUI.Label(new Rect(10, 228, 500, 24), $"IMU Rotation(euler): {currentRotation.eulerAngles}");
            GUI.Label(new Rect(10, 252, 500, 24), $"IMU Rotation(quaternion raw): w={currentRotation.w:F3} x={currentRotation.x:F3} y={currentRotation.y:F3} z={currentRotation.z:F3}");
        }
        if (tareMessageTimer > 0f)
        {
            GUI.Label(new Rect(10, 276, 500, 24), "-> IMU 영점 재조절(tare) 명령 전송함");
        }
    }
}
