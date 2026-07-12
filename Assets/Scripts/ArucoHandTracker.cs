// ArucoHandTracker.cs (멀티마커 버전)
//
// 손등/손바닥/손날/측면 등 여러 면에 각각 다른 ID의 ArUco 마커를 붙여두고,
// 매 프레임 "지금 보이는 마커들 중 가장 크게(=가장 정면으로) 보이는 것" 하나를 골라
// solvePnP로 위치+회전을 계산해서 Hand의 transform에 적용합니다.
//
// ArUco는 점 2개 방식과 달리 마커 하나만으로 위치+회전이 전부 나오기 때문에,
// IMU 없이도 동작합니다 (원한다면 IMU는 나중에 손가락 curl 값 융합이나 보조용으로만 써도 됨).
//
// 준비물:
// - NuGetForUnity로 OpenCvSharp4 + OpenCvSharp4.runtime.win 설치 (이미 되어있음)
// - 각 면마다 서로 다른 ID의 ArUco 마커 인쇄 (DICT_4X4_50 딕셔너리, 추천 크기 2cm 안팎)
// - 각 마커의 실제 크기를 자로 재서 정확히 입력

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.Rendering;
using OpenCvSharp;
using OpenCvSharp.Aruco;

[System.Serializable]
// 등속도(constant velocity) 모델 1차원 칼만 필터.
// 위치와 속도를 같이 추정해서, 측정값이 없는 프레임(인식 실패)에도
// 예측(Predict)만 계속 돌려서 자연스럽게 이어줍니다.
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
}

public class ArucoHandTracker : MonoBehaviour
{
    public enum InputSource { Webcam, VideoFile }

    [Header("입력 소스")]
    [Tooltip("Webcam: 실시간 웹캠. VideoFile: 미리 찍어둔 영상 파일로 테스트 (예: 60fps 촬영본 실험용)")]
    public InputSource inputSource = InputSource.Webcam;
    [Tooltip("VideoFile 모드일 때 재생할 영상 (mp4 등). Assets 폴더에 영상을 넣고 여기에 드래그")]
    public VideoClip videoClip;
    public bool loopVideo = true;

    [Header("마커 딕셔너리")]
    [Tooltip("ArUco 4x4_50이 기본이었는데, 오탐이 잦으면 AprilTag 36h11이 더 안정적일 수 있음")]
    public PredefinedDictionaryType dictionaryType = PredefinedDictionaryType.DictAprilTag_36h11;

    [Header("웹캠 설정")]
    public int requestedWidth = 1280;
    public int requestedHeight = 720;
    [Tooltip("웹캠에 요청할 FPS. 웹캠이 지원 안 하면 자동으로 가까운 값으로 맞춰짐")]
    public int requestedFPS = 60;

    [Header("카메라 내부 파라미터 (캘리브레이션 전 임시값)")]
    public double fx = 1000;
    public double fy = 1000;
    [Tooltip("켜두면 Cx/Cy는 실제 해상도의 절반으로 자동 계산됩니다 (직접 값을 안 맞춰도 됨)")]
    public bool autoComputePrincipalPoint = true;
    public double cx = 640;
    public double cy = 360;
    [Tooltip("Fx/Fy가 아직 대충 잡은 값이라 실제 거리랑 다를 수 있음. 실측 거리 ÷ 화면에 표시되는 거리로 보정값 계산해서 넣으세요 (예: 실제 40cm인데 화면상 20cm로 나오면 2.0)")]
    public float distanceScaleCorrection = 1.0f;
    private bool camMatrixConfigured = false;

    [Header("마커 목록 (면마다 하나씩 등록)")]
    public List<ArucoMarkerConfig> markers = new List<ArucoMarkerConfig>();

    [Header("적용 대상")]
    [Tooltip("여기에 위치/회전을 적용할 손(Hand) Transform")]
    public Transform targetTransform;
    [Tooltip("회전도 ArUco 결과로 적용할지 여부. 끄면 위치만 적용하고 회전은 그대로 둠(예: IMU가 따로 담당할 때)")]
    public bool applyRotation = true;

    [Header("스무딩 (회전용, 위치는 아래 칼만 필터가 담당)")]
    [Range(0f, 1f)]
    public float smoothingFactor = 0.3f;

    [Header("웹캠 미리보기 (선택사항)")]
    public RawImage previewImage;
    [Tooltip("체크하면 웹캠 화면이 거울처럼 좌우반전되어 보임 (인식/계산용 좌표는 그대로라 위치 추적엔 영향 없음)")]
    public bool mirrorPreview = true;

    [Header("디버그")]
    public bool showDebugWindow = true;
    public string activeMarkerFace = "-";
    public bool hasValidEstimate = false;

    [Header("칼만 필터 (위치 보정)")]
    [Tooltip("클수록 측정값을 더 신뢰 (반응 빠르지만 흔들릴 수 있음)")]
    public double kalmanProcessNoise = 4.0;
    [Tooltip("클수록 예측(속도 기반 추정)을 더 신뢰 (부드럽지만 반응 느려짐)")]
    public double kalmanMeasurementNoise = 0.5;
    [Tooltip("마지막 인식 후 이 시간(초)까지는 칼만 필터 예측값을 계속 적용. 넘기면 그 자리에 고정")]
    public float maxPredictOnlySeconds = 1.0f;

    private KalmanFilter1D kalmanX = new KalmanFilter1D();
    private KalmanFilter1D kalmanY = new KalmanFilter1D();
    private KalmanFilter1D kalmanZ = new KalmanFilter1D();
    private float lastFrameTimestamp = -1f;
    private float measuredFPS = 0f;
    private float lastValidEstimateTime = -999f;

    [Header("모션 블러로 ID 판독 실패 시 위치라도 이어가기")]
    [Tooltip("사각형 후보로 인정할 최소 크기 (화면 최대변 대비 비율). 기본 0.03인데, 멀리서도 rejected 후보로라도 잡히게 하려면 낮춰보세요 (예: 0.01~0.02)")]
    public float minMarkerPerimeterRate = 0.02f;
    [Tooltip("ID 판독은 실패했지만 사각형 후보(rejected)가 마지막 위치에서 이 픽셀거리 이내면, 같은 마커로 간주하고 위치만 계속 추정")]
    public float continuityMaxPixelDistance = 150f;
    public bool showContinuityAsValid = true;

    private ArucoMarkerConfig lastKnownConfig = null;
    private Point2f lastKnownImageCenter;
    private double lastKnownArea = 0;
    [Tooltip("연속성 추정 시, 마지막으로 본 크기 대비 이 배율 범위(예: 0.5~2.0배) 안에 있어야 같은 마커로 인정")]
    public float continuityAreaRatioMin = 0.5f;
    public float continuityAreaRatioMax = 2.0f;
    private bool hasLastKnownPosition = false;
    private bool isContinuityFallback = false;

    private WebCamTexture webcamTexture;

    // 영상 파일 입력용
    private VideoPlayer videoPlayer;
    private RenderTexture videoRenderTexture;
    private long lastVideoFrameIndex = -1;
    private AsyncGPUReadbackRequest pendingReadback;
    private bool hasPendingReadback = false;

    // 웹캠이든 영상이든 상관없이 공통으로 쓰는, "지금 프레임" 정보
    private int currentWidth, currentHeight;
    private bool currentIsPlaying;
    private Color32[] currentPixels;
    private bool hasNewFrameThisUpdate;

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

    // 화면에 마커 사각형을 그리기 위한 디버그용 저장
    private Point2f[] lastDrawnCorners = null;
    private int lastImageWidth, lastImageHeight;
    private Texture2D dotTexture;

    void Start()
    {
        if (inputSource == InputSource.Webcam)
        {
            webcamTexture = new WebCamTexture(requestedWidth, requestedHeight, requestedFPS);
            webcamTexture.Play();

            if (previewImage != null)
                previewImage.texture = webcamTexture;
        }
        else // VideoFile
        {
            if (videoClip == null)
            {
                Debug.LogError("[ArucoHandTracker] InputSource가 VideoFile인데 Video Clip이 비어있습니다.");
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

        // camMatrix는 실제 해상도를 알아야 정확히 만들 수 있어서, 첫 프레임 받은 뒤 Update()에서 생성
        distCoeffs = Mat.Zeros(5, 1, MatType.CV_64FC1);
    }

    void Update()
    {
        if (!UpdateCurrentFrame())
            return;

        // 실제 프레임 갱신 간격으로 FPS 실측 (요청한 FPS랑 실제는 다를 수 있어서)
        if (lastFrameTimestamp > 0f)
        {
            float instantFPS = 1f / (Time.time - lastFrameTimestamp);
            measuredFPS = Mathf.Lerp(measuredFPS, instantFPS, 0.1f); // 살짝 평활해서 안 튀게
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

        // 인식 성공 여부와 상관없이 매 프레임 예측은 항상 진행 (칼만 필터의 핵심)
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

        if (ids != null && ids.Length > 0)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ArucoMarkerConfig cfg = markers.Find(m => m.markerId == ids[i]);
                if (cfg == null) continue; // 등록 안 한 ID는 무시

                // 화면에 보이는 사각형 면적이 클수록(=더 가깝고 정면으로 보일수록) 신뢰도 높음
                double area = Cv2.ContourArea(corners[i]);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestConfig = cfg;
                    bestCorners = corners[i];
                }
            }
        }

        if (bestConfig != null)
        {
            if (bestConfig.markerSizeMeters <= 0f)
            {
                Debug.LogWarning($"[ArucoHandTracker] '{bestConfig.faceName}' (ID {bestConfig.markerId})의 Marker Size Meters가 0 이하입니다. Inspector에서 실제 크기(미터)를 입력해주세요.");
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
                ApplyPose(rvec, tvec);
            }

            activeMarkerFace = bestConfig.faceName;
            hasValidEstimate = true;
            isContinuityFallback = false;
            lastDrawnCorners = bestCorners;
            lastImageWidth = grayMat.Width;
            lastImageHeight = grayMat.Height;

            // 다음에 ID 판독이 실패해도 이어갈 수 있게, 이번에 확인된 위치/크기를 기억해둠
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

            // ID 판독은 실패했지만, 직전 위치 근처에 사각형 모양 후보(rejected)가 있으면
            // "같은 마커가 블러 때문에 ID만 못 읽힌 것"으로 보고 위치만 이어서 추정
            if (hasLastKnownPosition && rejected != null && rejected.Length > 0 &&
                (Time.time - lastValidEstimateTime) < maxPredictOnlySeconds)
            {
                Point2f[] bestCandidate = null;
                float bestDist = continuityMaxPixelDistance;

                foreach (var candidate in rejected)
                {
                    // 크기 조건: 마지막으로 본 마커 크기 대비 너무 작거나 크면(엉뚱한 물체) 제외
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
                        // 코너 순서가 원래 마커랑 90도씩 다를 수 있어 회전값은 못 믿음.
                        // 위치(tvec)만 취해서 칼만 필터에 넣고, 회전은 이번 프레임엔 갱신 안 함
                        Cv2.SolvePnP(objPointsInput, imgPointsInput, camMatrix, distCoeffs, rvec, tvec,
                            false, SolvePnPMethod.IPPE_SQUARE);

                        double tx = tvec.At<double>(0);
                        double ty = tvec.At<double>(1);
                        double tz = tvec.At<double>(2);
                        Vector3 rawPosition = new Vector3(-(float)tx, -(float)ty, (float)tz) * distanceScaleCorrection;

                        kalmanX.UpdateMeasurement(rawPosition.x);
                        kalmanY.UpdateMeasurement(rawPosition.y);
                        kalmanZ.UpdateMeasurement(rawPosition.z);
                    }

                    lastKnownImageCenter = ComputeCenter(bestCandidate); // 계속 이어서 추적할 수 있게 갱신
                    lastKnownArea = Cv2.ContourArea(bestCandidate);
                    lastValidEstimateTime = Time.time; // 연속성 추정도 "아직 살아있다"로 간주해서 타임아웃 연장
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

        // 인식 성공/실패와 상관없이, 칼만 필터가 추정한 위치를 적용
        // (실패한 지 너무 오래됐으면 더 이상 추측하지 않고 마지막 위치에 고정)
        bool withinPredictWindow = (Time.time - lastValidEstimateTime) < maxPredictOnlySeconds;
        if (targetTransform != null && kalmanX.IsInitialized && withinPredictWindow)
        {
            targetTransform.localPosition = new Vector3((float)kalmanX.Position, (float)kalmanY.Position, (float)kalmanZ.Position);
        }
    }

    private void ApplyPose(Mat rvec, Mat tvec)
    {
        double tx = tvec.At<double>(0);
        double ty = tvec.At<double>(1);
        double tz = tvec.At<double>(2);

        // OpenCV 카메라 좌표계(Y down) -> Unity(Y up) 변환
        Vector3 rawPosition = new Vector3(-(float)tx, -(float)ty, (float)tz) * distanceScaleCorrection;

        // 로드리게스 회전 벡터 -> 회전 행렬 -> Unity 쿼터니언
        Mat rotMat = new Mat();
        Cv2.Rodrigues(rvec, rotMat);
        Matrix4x4 m = Matrix4x4.identity;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r, c] = (float)rotMat.At<double>(r, c);
        // OpenCV(Y down, 오른손) -> Unity(Y up, 왼손) 좌표계 보정
        Quaternion rawRotation = Quaternion.LookRotation(
            new Vector3(m.m20, -m.m21, m.m22),
            new Vector3(-m.m10, m.m11, -m.m12));
        rotMat.Dispose();

        // 위치는 칼만 필터에 측정값으로 반영 (평활 + 예측을 한번에 처리)
        kalmanX.UpdateMeasurement(rawPosition.x);
        kalmanY.UpdateMeasurement(rawPosition.y);
        kalmanZ.UpdateMeasurement(rawPosition.z);

        // 회전은 기존처럼 단순 Slerp로 부드럽게 (회전용 칼만 필터는 훨씬 복잡해서 일단 보류)
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
        // 예전엔 픽셀 200만 개를 C# 반복문으로 한 개씩 복사+뒤집기 했는데,
        // 그게 프레임당 시간을 많이 잡아먹어서(고해상도일수록 심함) 통짜 메모리 복사로 교체.
        // Color32는 메모리상 R,G,B,A 1바이트씩이라 별도 변환 없이 그대로 복사 가능.
        int totalBytes = pixels.Length * 4;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            System.IntPtr srcPtr = handle.AddrOfPinnedObject();
            mat?.Dispose();
            mat = new Mat(height, width, MatType.CV_8UC4);
            // 소스(핀 고정된 배열) -> 매트로 한 번에 복사 (반복문 없음)
            var temp = new byte[totalBytes];
            System.Runtime.InteropServices.Marshal.Copy(srcPtr, temp, 0, totalBytes);
            System.Runtime.InteropServices.Marshal.Copy(temp, 0, mat.Data, totalBytes);
        }
        finally
        {
            handle.Free();
        }

        // Unity 텍스처는 아래→위 순서라, 위→아래(OpenCV 관례)로 뒤집기.
        // 이것도 C# 반복문 대신 OpenCV 네이티브 함수로 처리(훨씬 빠름)
        Cv2.Flip(mat, mat, FlipMode.X);
    }

    // 웹캠이든 영상 파일이든, "이번 프레임에 새 그림이 왔는지"를 확인하고
    // currentPixels/currentWidth/currentHeight를 채워줌. 새 프레임 없으면 false.
    private bool UpdateCurrentFrame()
    {
        if (inputSource == InputSource.Webcam)
        {
            if (webcamTexture == null || !webcamTexture.isPlaying || !webcamTexture.didUpdateThisFrame)
                return false;

            currentWidth = webcamTexture.width;
            currentHeight = webcamTexture.height;
            currentIsPlaying = true;
            currentPixels = webcamTexture.GetPixels32();
            return true;
        }
        else // VideoFile
        {
            if (videoPlayer == null || !videoPlayer.isPlaying)
            {
                currentIsPlaying = false;
                return false;
            }
            currentIsPlaying = true;

            // 이미 요청해둔 읽기가 끝났으면, 그 결과를 이번 프레임 데이터로 사용
            if (hasPendingReadback)
            {
                if (!pendingReadback.done)
                    return false; // 아직 GPU가 다 안 끝남, 이번 Update는 그냥 넘어감 (핵심: 여기서 안 멈추고 기다림)

                hasPendingReadback = false;
                if (pendingReadback.hasError)
                    return false;

                var data = pendingReadback.GetData<Color32>();
                if (currentPixels == null || currentPixels.Length != data.Length)
                    currentPixels = new Color32[data.Length];
                data.CopyTo(currentPixels);

                currentWidth = videoRenderTexture.width;
                currentHeight = videoRenderTexture.height;

                // 처리하는 김에, 다음에 쓸 새 프레임도 이미 준비됐으면 바로 다음 요청을 걸어둠
                TryRequestNextVideoReadback();
                return true;
            }

            // 아직 아무 요청도 안 걸어놨으면, 새 영상 프레임이 왔는지 보고 요청 걸기
            TryRequestNextVideoReadback();
            return false;
        }
    }

    private void TryRequestNextVideoReadback()
    {
        if (hasPendingReadback || videoPlayer == null) return;

        long frame = videoPlayer.frame;
        if (frame == lastVideoFrameIndex) return; // 아직 다음 영상 프레임 안 옴
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
                ? $"웹캠: {webcamTexture.width}x{webcamTexture.height} @ 실측 {measuredFPS:F1}fps (요청 {requestedFPS}fps)"
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
            if (mirrorPreview) normX = 1f - normX; // 화면이 거울모드로 뒤집혀 보이니 마커 위치도 맞춰서 반전
            float normY = p.Y / lastImageHeight;
            float sx = guiLeft + normX * guiWidth;
            float sy = guiTop + normY * guiHeight;
            GUI.DrawTexture(new UnityEngine.Rect(sx - 5, sy - 5, 10, 10), dotTexture);
        }
        GUI.color = prev;
    }
}
