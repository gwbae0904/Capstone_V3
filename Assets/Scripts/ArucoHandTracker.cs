using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.Rendering;
using OpenCvSharp;
using OpenCvSharp.Aruco;

[System.Serializable]
public class KalmanFilter1D
{
    private double x, v;           // 상태: 위치, 속도
    private double p_xx, p_xv, p_vv; // 오차 공분산 (2x2 대칭행렬을 3개 값으로)
    private bool initialized = false;

    [Tooltip("모델(등속도 가정)이 실제랑 얼마나 다를 거라 보는지. 크면 측정값을 더 신뢰(반응 빠름, 더 흔들림)")]
    public double processNoise = 4.0;
    [Tooltip("측정값(ArUco 결과)에 노이즈가 얼마나 낀다고 보는지. 크면 예측을 더 신뢰(부드러움, 반응 느림)")]
    public double measurementNoise = 0.5;

    public double Position => x;
    public double Velocity => v;

    public void Reset(double initialPosition)
    {
        x = initialPosition;
        v = 0;
        p_xx = 1; p_xv = 0; p_vv = 1;
        initialized = true;
    }

    public bool IsInitialized => initialized;

    public void Predict(double dt)
    {
        if (!initialized || dt <= 0) return;
        x += v * dt;
        double q = processNoise * dt;
        p_xx += 2 * dt * p_xv + dt * dt * p_vv + q;
        p_xv += dt * p_vv;
        p_vv += q;
    }

    public void UpdateMeasurement(double measurement)
    {
        if (!initialized) { Reset(measurement); return; }

        double residual = measurement - x;
        double s = p_xx + measurementNoise;
        double k_x = p_xx / s;
        double k_v = p_xv / s;

        x += k_x * residual;
        v += k_v * residual;

        double p_xx_new = p_xx - k_x * p_xx;
        double p_xv_new = p_xv - k_x * p_xv;
        double p_vv_new = p_vv - k_v * p_xv;
        p_xx = p_xx_new; p_xv = p_xv_new; p_vv = p_vv_new;
    }
}

[System.Serializable]
public class ArucoMarkerConfig
{
    [Tooltip("이 마커를 생성할 때 지정한 ID (0, 1, 2...)")]
    public int markerId = 0;
    [Tooltip("이 마커가 붙어있는 면 이름 (구분용, 로직에는 영향 없음)")]
    public string faceName = "BackOfHand";
    [Tooltip("이 마커의 실제 한 변 길이 (미터). 자로 정확히 재서 입력")]
    public float markerSizeMeters = 0.02f;

    [Tooltip("같은 면에 마커가 여러 개 있어서(예: 손등 3개) 서로 전환될 때 위치가 튀는 걸 줄이기 위한 " +
             "고정 위치 보정값(미터, 마커의 로컬 좌표계 기준). 마커가 지금 측정한 회전값만큼 자동으로 " +
             "돌려서 적용되므로, 손이 어느 각도로 돌아가 있어도 정확히 보정됩니다. 마커 하나를 " +
             "기준(0,0,0)으로 두고, 나머지는 그 기준과 같은 손 자세에서 보고 위치가 같아지도록 " +
             "실측해서 채우세요. 완전히 다른 방향을 보는 면(손바닥 vs 손등 등)끼리는 동시에 안 보이므로 " +
             "보통 0으로 둬도 됩니다.")]
    public Vector3 positionOffset = Vector3.zero;
}

public class ArucoHandTracker : MonoBehaviour
{
    public enum InputSource { Webcam, VideoFile }

    [Header("입력 소스")]
    [Tooltip("Webcam: 실시간 웹캠. VideoFile: 미리 찍어둔 영상 파일로 테스트")]
    public InputSource inputSource = InputSource.Webcam;
    [Tooltip("VideoFile 모드일 때 재생할 영상")]
    public VideoClip videoClip;
    public bool loopVideo = true;

    [Header("마커 딕셔너리")]
    public PredefinedDictionaryType dictionaryType = PredefinedDictionaryType.DictAprilTag_36h11;

    [Header("웹캠 설정")]
    [Tooltip("이 이름을 포함하는 카메라를 최우선으로 찾습니다 (예: 'FHD60F'). 대소문자 구분 안 함. " +
             "정확한 이름을 모르면 일단 Play 한 번 눌러서 Console에 찍히는 '사용 가능한 카메라 목록'을 " +
             "확인하세요. 비워두면 이 우선순위는 건너뛰고 기존 방식(iVCam → 첫 번째 카메라)으로 찾습니다.")]
    public string preferredCameraName = "";
    [Tooltip("체크를 끄면 iVCam을 무시하고 노트북 내장 기본 웹캠을 강제로 켭니다. (Preferred Camera Name을 " +
             "채워서 이미 찾았으면 이 옵션은 건너뜁니다)")]
    public bool useIVCam = true;

    public int requestedWidth = 1280;
    public int requestedHeight = 960;
    public int requestedFPS = 60;

    [Header("카메라 내부 파라미터")]
    public double fx = 1000;
    public double fy = 1000;
    public bool autoComputePrincipalPoint = true;
    public double cx = 640;
    public double cy = 480;
    public float distanceScaleCorrection = 1.0f;
    private bool camMatrixConfigured = false;

    [Header("깊이(거리) 반전")]
    [Tooltip("켜면 손이 카메라에 가까워질수록 가상 손이 화면에서 멀어지고, 손이 카메라에서 멀어질수록 가상 손이 가까워지는 반대 방향으로 동작합니다.")]
    public bool invertDepth = false;
    [Tooltip("반전의 기준이 되는 실측 거리(미터). 이 거리에 있을 때는 반전 여부와 상관없이 위치가 그대로 유지되고, 이보다 가까워지거나 멀어지면 반대 방향으로 움직입니다.")]
    public float depthReferenceMeters = 0.35f;

    [Header("마커 목록")]
    public List<ArucoMarkerConfig> markers = new List<ArucoMarkerConfig>();

    [Header("적용 대상")]
    public Transform targetTransform;
    public bool applyRotation = true;

    [Header("스무딩 (회전용)")]
    [Range(0f, 1f)]
    public float smoothingFactor = 0.3f;

    [Header("웹캠 미리보기")]
    public RawImage previewImage;
    public bool mirrorPreview = true;

    [Header("디버그")]
    public bool showDebugWindow = true;
    public string activeMarkerFace = "-";
    public bool hasValidEstimate = false;
    [Header("칼만 필터 (위치 보정)")]
    public double kalmanProcessNoise = 4.0;
    public double kalmanMeasurementNoise = 0.5;
    public float maxPredictOnlySeconds = 1.0f;

    private KalmanFilter1D kalmanX = new KalmanFilter1D();
    private KalmanFilter1D kalmanY = new KalmanFilter1D();
    private KalmanFilter1D kalmanZ = new KalmanFilter1D();
    private float lastFrameTimestamp = -1f;
    private float measuredFPS = 0f;
    private float lastValidEstimateTime = -999f;

    [Header("여러 마커 동시 인식 시 선택 안정화")]
    [Tooltip("손등처럼 마커 여러 개가 한 앵글에 동시에 잡힐 때, 지금 추적 중인 마커보다 다른 마커가 " +
             "이 비율만큼 더 커야만 그쪽으로 전환합니다 (예: 1.3 = 30% 더 커야 전환). 값이 1.0에 " +
             "가까울수록 비슷한 크기의 마커 사이에서 자주 전환되어 위치가 튈 수 있습니다.")]
    public float markerSwitchThresholdRatio = 1.3f;
    private int currentlyTrackedMarkerId = -1;

    [Tooltip("Position Offset을 회전시켜 적용할 때, 방향이 반대로(예: 90도 돌아갔을 때 부호가 반대로) " +
             "나오면 이 체크박스를 켜서 뒤집어보세요.")]
    public bool invertOffsetRotationDirection = false;

    [Tooltip("마커 회전값(rawRotation) 자체의 X/Y/Z 축을 각각 반전. IMU의 Invert X/Y/Z와 같은 원리. " +
             "특정 각도에서만 부호가 이상해지는 경우, 전체를 뒤집는 것보다 이걸로 축 하나씩 실험해보세요.")]
    public bool invertMarkerRotX = false;
    public bool invertMarkerRotY = false;
    public bool invertMarkerRotZ = false;

    // 마커 회전(rawRotation)의 x/y/z 축을 각각 반전. IMU의 ConvertImuQuaternion과 같은 방식.
    private Quaternion InvertMarkerRotationAxes(Quaternion q)
    {
        float x = invertMarkerRotX ? -q.x : q.x;
        float y = invertMarkerRotY ? -q.y : q.y;
        float z = invertMarkerRotZ ? -q.z : q.z;
        return new Quaternion(x, y, z, q.w);
    }

    // Position Offset을 마커의 현재 회전으로 돌려서 적용. invertOffsetRotationDirection이
    // 켜져 있으면 회전의 역방향을 사용 (특정 각도에서 부호가 반대로 나오는 경우 대응용).
    // 디버그용으로 마지막 계산값도 같이 기록해둠 (OnGUI에 표시).
    public Vector3 debugLastMarkerRotationEuler;
    public Vector3 debugLastAppliedOffset;
    private Vector3 RotateOffset(Quaternion markerRotation, Vector3 offset)
    {
        Quaternion effectiveRotation = invertOffsetRotationDirection ? Quaternion.Inverse(markerRotation) : markerRotation;
        Vector3 result = effectiveRotation * offset;
        debugLastMarkerRotationEuler = markerRotation.eulerAngles;
        debugLastAppliedOffset = result;
        return result;
    }

    [Header("모션 블러로 ID 판독 실패 시 위치라도 이어가기")]
    public float minMarkerPerimeterRate = 0.02f;
    public float continuityMaxPixelDistance = 150f;
    public bool showContinuityAsValid = true;

    private ArucoMarkerConfig lastKnownConfig = null;
    private Point2f lastKnownImageCenter;
    private double lastKnownArea = 0;
    public float continuityAreaRatioMin = 0.5f;
    public float continuityAreaRatioMax = 2.0f;
    private bool hasLastKnownPosition = false;
    private bool isContinuityFallback = false;

    private WebCamTexture webcamTexture;

    private VideoPlayer videoPlayer;
    private RenderTexture videoRenderTexture;
    private long lastVideoFrameIndex = -1;
    private AsyncGPUReadbackRequest pendingReadback;
    private bool hasPendingReadback = false;

    private int currentWidth, currentHeight;
    private Color32[] currentPixels;

    private Mat frameMat;
    private Mat grayMat;
    private Mat camMatrix;
    private Mat distCoeffs;
    private OpenCvSharp.Aruco.Dictionary arucoDict;
    private OpenCvSharp.Aruco.DetectorParameters detectorParams;
    private ArucoDetector arucoDetector;
    private bool aspectConfigured = false;

    private Quaternion smoothedRotation = Quaternion.identity;
    private bool smoothedInitialized = false;

    private Point2f[] lastDrawnCorners = null;
    private int lastImageWidth, lastImageHeight;
    private Texture2D dotTexture;

    // 깊이(Z)를 기준 거리 중심으로 반전시킴. invertDepth가 꺼져있으면 원본 그대로 반환.
    // rawZ가 기준거리보다 작으면(카메라에 더 가까우면) 결과는 기준거리보다 커지고(더 멀어짐),
    // rawZ가 기준거리보다 크면(카메라에서 더 멀면) 결과는 기준거리보다 작아짐(더 가까워짐).
    private float ApplyDepthInversion(float rawZ)
    {
        if (!invertDepth) return rawZ;
        return 2f * depthReferenceMeters - rawZ;
    }

    void Start()
    {
        if (inputSource == InputSource.Webcam)
        {
            requestedWidth = 1280;
            requestedHeight = 960;
            requestedFPS = 60;

            // 지금 PC에 연결된 카메라 전부를 콘솔에 찍어둠 - 정확한 실제 이름(Windows가 인식하는
            // 이름)을 확인할 때 유용함. 마케팅 이름(예: "FHD60F")과 실제 디바이스 이름이 다를 수 있음.
            string deviceList = string.Join(", ", System.Array.ConvertAll(WebCamTexture.devices, d => d.name));
            UnityEngine.Debug.Log($"[ArucoHandTracker] 사용 가능한 카메라 목록: {deviceList}");

            string finalCamName = "";

            // 1순위: Preferred Camera Name에 지정한 이름을 포함하는 카메라
            if (!string.IsNullOrEmpty(preferredCameraName))
            {
                foreach (var device in WebCamTexture.devices)
                {
                    if (device.name.ToLower().Contains(preferredCameraName.ToLower()))
                    {
                        finalCamName = device.name;
                        UnityEngine.Debug.Log("[ArucoHandTracker] 지정한 카메라를 찾았습니다: " + finalCamName);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(finalCamName))
                {
                    UnityEngine.Debug.LogWarning($"[ArucoHandTracker] Preferred Camera Name '{preferredCameraName}'을(를) 포함하는 카메라를 못 찾았습니다. 다음 우선순위로 넘어갑니다.");
                }
            }

            // 2순위: iVCam
            if (string.IsNullOrEmpty(finalCamName) && useIVCam)
            {
                foreach (var device in WebCamTexture.devices)
                {
                    if (device.name.ToLower().Contains("ivcam"))
                    {
                        finalCamName = device.name;
                        UnityEngine.Debug.Log("[ArucoHandTracker] 스마트 자동 탐색 - iVCam을 찾았습니다: " + finalCamName);
                        break;
                    }
                }
            }

            // 3순위: 첫 번째로 찾은(iVCam이 아닌) 카메라, 그마저 없으면 그냥 첫 번째
            if (string.IsNullOrEmpty(finalCamName) && WebCamTexture.devices.Length > 0)
            {
                foreach (var device in WebCamTexture.devices)
                {
                    if (!device.name.ToLower().Contains("ivcam"))
                    {
                        finalCamName = device.name;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(finalCamName))
                {
                    finalCamName = WebCamTexture.devices[0].name;
                }
                UnityEngine.Debug.Log("[ArucoHandTracker] 기본 카메라를 사용합니다: " + finalCamName);
            }

            webcamTexture = new WebCamTexture(finalCamName, requestedWidth, requestedHeight, requestedFPS);
            webcamTexture.requestedFPS = requestedFPS;
            webcamTexture.Play();

            if (previewImage != null)
                previewImage.texture = webcamTexture;
        }
        else
        {
            if (videoClip == null)
            {
                UnityEngine.Debug.LogError("[ArucoHandTracker] InputSource가 VideoFile인데 Video Clip이 비어있습니다.");
            }
            else
            {
                videoRenderTexture = new RenderTexture((int)videoClip.width, (int)videoClip.height, 0, RenderTextureFormat.ARGB32);

                videoPlayer = gameObject.AddComponent<VideoPlayer>();
                videoPlayer.playOnAwake = false;
                videoPlayer.isLooping = loopVideo;
                videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                videoPlayer.targetTexture = videoRenderTexture;
                videoPlayer.source = VideoSource.VideoClip;
                videoPlayer.clip = videoClip;
                videoPlayer.Play();

                if (previewImage != null)
                    previewImage.texture = videoRenderTexture;
            }
        }

        if (previewImage != null)
            previewImage.uvRect = mirrorPreview ? new UnityEngine.Rect(1, 0, -1, 1) : new UnityEngine.Rect(0, 0, 1, 1);

        arucoDict = CvAruco.GetPredefinedDictionary(dictionaryType);
        detectorParams = new OpenCvSharp.Aruco.DetectorParameters();
        detectorParams.UseAruco3Detection = true;
        detectorParams.MinMarkerPerimeterRate = minMarkerPerimeterRate;
        arucoDetector = new ArucoDetector(arucoDict, detectorParams, new OpenCvSharp.Aruco.RefineParameters());

        kalmanX.processNoise = kalmanY.processNoise = kalmanZ.processNoise = kalmanProcessNoise;
        kalmanX.measurementNoise = kalmanY.measurementNoise = kalmanZ.measurementNoise = kalmanMeasurementNoise;

        distCoeffs = Mat.Zeros(5, 1, MatType.CV_64FC1);
    }

    void Update()
    {
        if (!UpdateCurrentFrame())
            return;

        if (lastFrameTimestamp > 0f)
        {
            float instantFPS = 1f / (Time.time - lastFrameTimestamp);
            measuredFPS = Mathf.Lerp(measuredFPS, instantFPS, 0.1f);
        }
        lastFrameTimestamp = Time.time;

        if (!aspectConfigured && currentWidth > 16 && previewImage != null)
        {
            ConfigureAspectRatio();
            aspectConfigured = true;
        }

        if (!camMatrixConfigured && currentWidth > 16)
        {
            if (autoComputePrincipalPoint)
            {
                cx = currentWidth / 2.0;
                cy = currentHeight / 2.0;
            }
            camMatrix = new Mat(3, 3, MatType.CV_64FC1);
            camMatrix.Set<double>(0, 0, fx);
            camMatrix.Set<double>(0, 1, 0);
            camMatrix.Set<double>(0, 2, cx);
            camMatrix.Set<double>(1, 0, 0);
            camMatrix.Set<double>(1, 1, fy);
            camMatrix.Set<double>(1, 2, cy);
            camMatrix.Set<double>(2, 0, 0);
            camMatrix.Set<double>(2, 1, 0);
            camMatrix.Set<double>(2, 2, 1);
            camMatrixConfigured = true;
        }

        PixelsToMat(currentPixels, currentWidth, currentHeight, ref frameMat);
        if (grayMat == null) grayMat = new Mat();
        Cv2.CvtColor(frameMat, grayMat, ColorConversionCodes.RGBA2GRAY);

        float dt = Time.deltaTime;
        kalmanX.Predict(dt);
        kalmanY.Predict(dt);
        kalmanZ.Predict(dt);

        Point2f[][] corners;
        int[] ids;
        Point2f[][] rejected;
        arucoDetector.DetectMarkers(grayMat, out corners, out ids, out rejected);

        ArucoMarkerConfig bestConfig = null;
        Point2f[] bestCorners = null;
        double bestArea = -1;
        int bestMarkerIdThisFrame = -1;

        if (ids != null && ids.Length > 0)
        {
            // 손등처럼 마커 여러 개가 한 앵글에 동시에 잡히는 경우, "이번 프레임에 제일 큰 것"만
            // 단순 비교하면 크기가 엇비슷할 때 프레임마다 다른 마커로 휙휙 전환되기 쉬움. 마커마다
            // 물리적으로 다른 위치에 붙어있으므로, 전환될 때마다 위치가 살짝 튀는 것처럼 보임.
            // -> 지금 추적 중인 마커가 여전히 보이면, 다른 마커가 "확실히"(markerSwitchThresholdRatio
            // 배 이상) 더 크지 않은 이상 계속 지금 마커를 우선시함.
            double currentTrackedArea = -1;
            int currentTrackedIndex = -1;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == currentlyTrackedMarkerId)
                {
                    currentTrackedIndex = i;
                    currentTrackedArea = Cv2.ContourArea(corners[i]);
                    break;
                }
            }

            for (int i = 0; i < ids.Length; i++)
            {
                ArucoMarkerConfig cfg = markers.Find(m => m.markerId == ids[i]);
                if (cfg == null) continue;

                double area = Cv2.ContourArea(corners[i]);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestConfig = cfg;
                    bestCorners = corners[i];
                    bestMarkerIdThisFrame = ids[i];
                }
            }

            // 히스테리시스 적용: 후보가 지금 추적 중인 마커랑 다르고, 그 차이가 기준 비율보다
            // 작으면 전환을 취소하고 원래 마커를 계속 사용
            if (currentTrackedIndex != -1 && bestMarkerIdThisFrame != currentlyTrackedMarkerId
                && bestArea < currentTrackedArea * markerSwitchThresholdRatio)
            {
                bestConfig = markers.Find(m => m.markerId == currentlyTrackedMarkerId);
                bestCorners = corners[currentTrackedIndex];
                bestMarkerIdThisFrame = currentlyTrackedMarkerId;
            }
        }

        if (bestConfig != null)
            currentlyTrackedMarkerId = bestMarkerIdThisFrame;

        if (bestConfig != null)
        {
            if (bestConfig.markerSizeMeters <= 0f)
            {
                UnityEngine.Debug.LogWarning($"[ArucoHandTracker] '{bestConfig.faceName}' (ID {bestConfig.markerId})의 Marker Size Meters가 0 이하입니다. Inspector에서 실제 크기(미터)를 입력해주세요.");
                bestConfig = null;
            }
        }

        if (bestConfig != null)
        {
            double half = bestConfig.markerSizeMeters / 2.0;
            Point3f[] objectPoints = new Point3f[]
            {
                new Point3f((float)-half, (float)half, 0),
                new Point3f((float)half, (float)half, 0),
                new Point3f((float)half, (float)-half, 0),
                new Point3f((float)-half, (float)-half, 0),
            };

            using (Mat rvec = new Mat())
            using (Mat tvec = new Mat())
            using (var objPointsInput = InputArray.Create(objectPoints))
            using (var imgPointsInput = InputArray.Create(bestCorners))
            {
                Cv2.SolvePnP(objPointsInput, imgPointsInput, camMatrix, distCoeffs, rvec, tvec,
                    false, SolvePnPMethod.IPPE_SQUARE);
                ApplyPose(rvec, tvec, bestConfig);
            }

            activeMarkerFace = bestConfig.faceName;
            hasValidEstimate = true;
            isContinuityFallback = false;
            lastDrawnCorners = bestCorners;
            lastImageWidth = grayMat.Width;
            lastImageHeight = grayMat.Height;

            lastKnownConfig = bestConfig;
            lastKnownImageCenter = ComputeCenter(bestCorners);
            lastKnownArea = Cv2.ContourArea(bestCorners);
            hasLastKnownPosition = true;
        }
        else
        {
            activeMarkerFace = "-";
            hasValidEstimate = false;
            lastDrawnCorners = null;
            isContinuityFallback = false;

            if (hasLastKnownPosition && rejected != null && rejected.Length > 0 &&
                (Time.time - lastValidEstimateTime) < maxPredictOnlySeconds)
            {
                Point2f[] bestCandidate = null;
                float bestDist = continuityMaxPixelDistance;

                foreach (var candidate in rejected)
                {
                    double candidateArea = Cv2.ContourArea(candidate);
                    if (lastKnownArea > 0)
                    {
                        double ratio = candidateArea / lastKnownArea;
                        if (ratio < continuityAreaRatioMin || ratio > continuityAreaRatioMax)
                            continue;
                    }

                    Point2f center = ComputeCenter(candidate);
                    float dist = Distance2f(center, lastKnownImageCenter);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCandidate = candidate;
                    }
                }

                if (bestCandidate != null)
                {
                    double half = lastKnownConfig.markerSizeMeters / 2.0;
                    Point3f[] objectPoints = new Point3f[]
                    {
                        new Point3f((float)-half, (float)half, 0),
                        new Point3f((float)half, (float)half, 0),
                        new Point3f((float)half, (float)-half, 0),
                        new Point3f((float)-half, (float)-half, 0),
                    };

                    using (Mat rvec = new Mat())
                    using (Mat tvec = new Mat())
                    using (var objPointsInput = InputArray.Create(objectPoints))
                    using (var imgPointsInput = InputArray.Create(bestCandidate))
                    {
                        Cv2.SolvePnP(objPointsInput, imgPointsInput, camMatrix, distCoeffs, rvec, tvec,
                            false, SolvePnPMethod.IPPE_SQUARE);

                        double tx = tvec.At<double>(0);
                        double ty = tvec.At<double>(1);
                        double tz = tvec.At<double>(2);
                        float depthZ = ApplyDepthInversion((float)tz);
                        Vector3 rawPosition = new Vector3(-(float)tx, -(float)ty, depthZ) * distanceScaleCorrection;

                        // 연속성 추정 중엔 ID가 확정된 게 아니지만, 마지막으로 확정됐던 lastKnownConfig의
                        // 오프셋을 그대로 재사용 (거의 같은 마커일 가능성이 높으므로). 손이 돌아간 상태에서도
                        // 오프셋 방향이 같이 돌아가도록, 이번에 측정된 회전값만큼 돌려서 더함.
                        Mat fallbackRotMat = new Mat();
                        Cv2.Rodrigues(rvec, fallbackRotMat);
                        Matrix4x4 fm = Matrix4x4.identity;
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++)
                                fm[r, c] = (float)fallbackRotMat.At<double>(r, c);
                        Quaternion fallbackRotation = Quaternion.LookRotation(
                            new Vector3(fm.m20, -fm.m21, fm.m22),
                            new Vector3(-fm.m10, fm.m11, -fm.m12));
                        fallbackRotMat.Dispose();
                        fallbackRotation = InvertMarkerRotationAxes(fallbackRotation);

                        rawPosition += RotateOffset(fallbackRotation, lastKnownConfig.positionOffset);

                        kalmanX.UpdateMeasurement(rawPosition.x);
                        kalmanY.UpdateMeasurement(rawPosition.y);
                        kalmanZ.UpdateMeasurement(rawPosition.z);
                    }

                    lastKnownImageCenter = ComputeCenter(bestCandidate);
                    lastKnownArea = Cv2.ContourArea(bestCandidate);
                    lastValidEstimateTime = Time.time;
                    isContinuityFallback = true;
                    if (showContinuityAsValid)
                    {
                        activeMarkerFace = lastKnownConfig.faceName + " (추정)";
                        hasValidEstimate = true;
                    }
                    lastDrawnCorners = bestCandidate;
                    lastImageWidth = grayMat.Width;
                    lastImageHeight = grayMat.Height;
                }
            }
        }

        bool withinPredictWindow = (Time.time - lastValidEstimateTime) < maxPredictOnlySeconds;
        if (targetTransform != null && kalmanX.IsInitialized && withinPredictWindow)
        {
            targetTransform.localPosition = new Vector3((float)kalmanX.Position, (float)kalmanY.Position, (float)kalmanZ.Position);
        }
    }

    private void ApplyPose(Mat rvec, Mat tvec, ArucoMarkerConfig config)
    {
        double tx = tvec.At<double>(0);
        double ty = tvec.At<double>(1);
        double tz = tvec.At<double>(2);

        float depthZ = ApplyDepthInversion((float)tz);
        Vector3 rawPosition = new Vector3(-(float)tx, -(float)ty, depthZ) * distanceScaleCorrection;

        Mat rotMat = new Mat();
        Cv2.Rodrigues(rvec, rotMat);
        Matrix4x4 m = Matrix4x4.identity;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = (float)rotMat.At<double>(r, c);
        Quaternion rawRotation = Quaternion.LookRotation(
            new Vector3(m.m20, -m.m21, m.m22),
            new Vector3(-m.m10, m.m11, -m.m12));
        rotMat.Dispose();
        rawRotation = InvertMarkerRotationAxes(rawRotation);

        // 같은 면에 마커가 여러 개 있어서 서로 전환될 때 튀는 걸 줄이기 위한 고정 오프셋.
        // 손이 돌아간 상태에서도 오프셋 방향이 같이 돌아가도록, 이 마커가 지금 측정한
        // 회전값(rawRotation)만큼 오프셋을 돌려서 더함 (별도의 축 보정/캘리브레이션 불필요 -
        // 이미 계산된 rawRotation을 그대로 재사용하는 것뿐).
        rawPosition += RotateOffset(rawRotation, config.positionOffset);

        kalmanX.UpdateMeasurement(rawPosition.x);
        kalmanY.UpdateMeasurement(rawPosition.y);
        kalmanZ.UpdateMeasurement(rawPosition.z);

        if (!smoothedInitialized)
        {
            smoothedRotation = rawRotation;
            smoothedInitialized = true;
        }
        else
        {
            smoothedRotation = Quaternion.Slerp(smoothedRotation, rawRotation, smoothingFactor);
        }

        lastValidEstimateTime = Time.time;

        if (targetTransform != null && applyRotation)
        {
            targetTransform.localRotation = smoothedRotation;
        }
    }

    private Point2f ComputeCenter(Point2f[] corners)
    {
        float sx = 0, sy = 0;
        foreach (var c in corners) { sx += c.X; sy += c.Y; }
        return new Point2f(sx / corners.Length, sy / corners.Length);
    }

    private float Distance2f(Point2f a, Point2f b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private void ConfigureAspectRatio()
    {
        AspectRatioFitter fitter = previewImage.GetComponent<AspectRatioFitter>();
        if (fitter == null)
            fitter = previewImage.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = (float)currentWidth / currentHeight;
    }

    private void PixelsToMat(Color32[] pixels, int width, int height, ref Mat mat)
    {
        int totalBytes = pixels.Length * 4;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            System.IntPtr srcPtr = handle.AddrOfPinnedObject();
            mat?.Dispose();
            mat = new Mat(height, width, MatType.CV_8UC4);
            var temp = new byte[totalBytes];
            System.Runtime.InteropServices.Marshal.Copy(srcPtr, temp, 0, totalBytes);
            System.Runtime.InteropServices.Marshal.Copy(temp, 0, mat.Data, totalBytes);
        }
        finally
        {
            handle.Free();
        }

        Cv2.Flip(mat, mat, FlipMode.X);
    }

    private bool UpdateCurrentFrame()
    {
        if (inputSource == InputSource.Webcam)
        {
            if (webcamTexture == null || !webcamTexture.isPlaying || !webcamTexture.didUpdateThisFrame)
                return false;

            currentWidth = webcamTexture.width;
            currentHeight = webcamTexture.height;
            currentPixels = webcamTexture.GetPixels32();
            return true;
        }
        else
        {
            if (videoPlayer == null || !videoPlayer.isPlaying)
            {
                return false;
            }

            if (hasPendingReadback)
            {
                if (!pendingReadback.done)
                    return false;

                hasPendingReadback = false;
                if (pendingReadback.hasError)
                    return false;

                var data = pendingReadback.GetData<Color32>();
                if (currentPixels == null || currentPixels.Length != data.Length)
                    currentPixels = new Color32[data.Length];
                data.CopyTo(currentPixels);

                currentWidth = videoRenderTexture.width;
                currentHeight = videoRenderTexture.height;

                TryRequestNextVideoReadback();
                return true;
            }

            TryRequestNextVideoReadback();
            return false;
        }
    }

    private void TryRequestNextVideoReadback()
    {
        if (hasPendingReadback || videoPlayer == null) return;

        long frame = videoPlayer.frame;
        if (frame == lastVideoFrameIndex) return;
        lastVideoFrameIndex = frame;

        pendingReadback = AsyncGPUReadback.Request(videoRenderTexture);
        hasPendingReadback = true;
    }

    void OnDestroy()
    {
        if (webcamTexture != null) webcamTexture.Stop();
        if (videoPlayer != null) videoPlayer.Stop();
        if (videoRenderTexture != null) videoRenderTexture.Release();
        frameMat?.Dispose();
        grayMat?.Dispose();
        camMatrix?.Dispose();
        distCoeffs?.Dispose();
        if (dotTexture != null) Destroy(dotTexture);
    }

    void OnGUI()
    {
        if (!showDebugWindow) return;

        string webcamStatus;
        if (inputSource == InputSource.Webcam)
        {
            webcamStatus = (webcamTexture != null && webcamTexture.isPlaying)
                ? $"웹캠: {webcamTexture.deviceName} — {webcamTexture.width}x{webcamTexture.height} @ 실측 {measuredFPS:F1}fps (요청 {requestedFPS}fps)"
                : "웹캠: 아직 재생 안 됨";
        }
        else
        {
            webcamStatus = (videoPlayer != null && videoPlayer.isPlaying)
                ? $"영상: {currentWidth}x{currentHeight} @ 실측 {measuredFPS:F1}fps (원본 {videoClip?.frameRate:F0}fps) frame {videoPlayer.frame}"
                : "영상: 아직 재생 안 됨";
        }
        GUI.Label(new UnityEngine.Rect(10, 100, 500, 26), webcamStatus);
        GUI.Label(new UnityEngine.Rect(10, 128, 500, 26), $"Active marker: {activeMarkerFace}  (valid: {hasValidEstimate}, 연속성추정: {isContinuityFallback})");
        if (targetTransform != null)
            GUI.Label(new UnityEngine.Rect(10, 156, 500, 26), $"Position: {targetTransform.position}");
        GUI.Label(new UnityEngine.Rect(10, 184, 600, 26), $"마커 회전(euler): {debugLastMarkerRotationEuler}  적용된 오프셋: {debugLastAppliedOffset}");

        DrawMarkerOverlay();
    }

    private void DrawMarkerOverlay()
    {
        if (lastDrawnCorners == null || previewImage == null || lastImageWidth <= 0) return;

        Vector3[] worldCorners = new Vector3[4];
        previewImage.rectTransform.GetWorldCorners(worldCorners);
        Canvas canvas = previewImage.canvas;
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

        Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, worldCorners[0]);
        Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, worldCorners[2]);

        float guiLeft = bl.x;
        float guiTop = Screen.height - tr.y;
        float guiWidth = tr.x - bl.x;
        float guiHeight = (Screen.height - bl.y) - guiTop;

        if (dotTexture == null)
        {
            dotTexture = new Texture2D(1, 1);
            dotTexture.SetPixel(0, 0, Color.white);
            dotTexture.Apply();
        }

        Color prev = GUI.color;
        GUI.color = Color.cyan;
        for (int i = 0; i < lastDrawnCorners.Length; i++)
        {
            Point2f p = lastDrawnCorners[i];
            float normX = p.X / lastImageWidth;
            if (mirrorPreview) normX = 1f - normX;
            float normY = p.Y / lastImageHeight;
            float sx = guiLeft + normX * guiWidth;
            float sy = guiTop + normY * guiHeight;
            GUI.DrawTexture(new UnityEngine.Rect(sx - 5, sy - 5, 10, 10), dotTexture);
        }
        GUI.color = prev;
    }
}
