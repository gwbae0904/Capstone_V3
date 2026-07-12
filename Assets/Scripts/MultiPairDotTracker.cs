// MultiPairDotTracker.cs
//
// 손의 여러 면(손등/손바닥/손날/수직거리용)에 각각 서로 다른 색 점 2개씩 붙여서,
// 매 프레임 "지금 가장 잘 보이는 쌍"을 골라 IMU 회전값으로 원근 단축(foreshortening)을
// 보정한 depth를 계산합니다.
//
// 준비물:
// - 색 점 8개 (쌍마다 2개, 서로 다른 색 8개) 스티커/마커
// - 각 쌍의 실제 거리(자로 측정, 미터 단위)
// - 각 쌍의 "손 로컬 좌표계 기준 방향 벡터" (IMU 보정용 - 아래 설명 참고)
// - IMU(MPU6050)로부터 매 프레임 갱신되는 Quaternion (아직 없으면 Quaternion.identity로 테스트 가능)
//
// [로컬 방향 벡터란?]
// 예를 들어 손등 쌍의 두 점이 손목 기준 "위쪽" 방향으로 나란히 붙어있다면 (0,1,0),
// 손날 쌍이 "옆쪽" 방향이면 (1,0,0) 이런 식으로, 손이 기준 자세(T-pose 등)일 때
// 그 쌍의 두 점을 잇는 벡터가 손 좌표계에서 어느 축을 향하는지를 나타냅니다.
// 정확한 값을 모르면 일단 대략적인 방향으로 넣고 나중에 캘리브레이션으로 다듬으면 됩니다.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using OpenCvSharp;

[System.Serializable]
public class MarkerPairConfig
{
    public string pairName = "BackOfHand";

    [Header("이 쌍(점 2개)의 공통 색상 HSV 범위")]
    public Vector3 colorHsvLow = new Vector3(0, 100, 100);
    public Vector3 colorHsvHigh = new Vector3(10, 255, 255);

    [Header("실측값")]
    [Tooltip("이 쌍의 두 점 사이 실제 거리 (미터)")]
    public float realDistanceMeters = 0.03f;
    [Tooltip("기준 자세일 때 이 쌍이 손 좌표계에서 향하는 방향 (정규화 안 해도 됨, 자동 정규화됨)")]
    public Vector3 localDirection = Vector3.up;

    [Tooltip("이 값보다 픽셀 면적이 작은 blob은 노이즈로 취급해 무시")]
    public int minBlobArea = 20;
}

public class MultiPairDotTracker : MonoBehaviour
{
    [Header("웹캠 설정")]
    [Tooltip("해상도를 높이면 화질은 좋아지지만 처리 속도가 느려질 수 있음. 웹캠이 지원하는 해상도인지 확인 필요")]
    public int requestedWidth = 1280;
    public int requestedHeight = 720;

    [Header("카메라 내부 파라미터 (캘리브레이션 전 임시값)")]
    public double fx = 600;
    public double fy = 600;
    public double cx = 320;
    public double cy = 240;

    [Header("마커 쌍 설정 (최대 4개 정도 권장)")]
    public List<MarkerPairConfig> pairs = new List<MarkerPairConfig>();

    [Header("IMU 연동")]
    [Tooltip("MPU6050에서 오는 현재 회전값. 아직 IMU 연결 전이면 비워두면 Quaternion.identity로 동작")]
    public IImuSource imuSource;

    [Header("적용 대상")]
    public Transform targetTransform;

    [Header("스무딩")]
    [Range(0f, 1f)]
    [Tooltip("낮을수록 더 부드럽지만 반응이 느려짐, 높을수록 반응은 빠르지만 흔들림 있음")]
    public float smoothingFactor = 0.3f;

    [Header("디버그")]
    public bool showDebugWindow = true;
    public string activePairName = "-";
    public bool hasValidEstimate = false;

    [Header("웹캠 미리보기 (선택사항)")]
    [Tooltip("여기에 UI RawImage를 연결하면 그 화면에 실시간 웹캠 영상이 표시됩니다. 비워두면 미리보기 없이 값 계산만 합니다.")]
    public RawImage previewImage;

    private WebCamTexture webcamTexture;
    private Mat frameMat;
    private Mat hsvMat;
    private Vector3 smoothedPosition;
    private bool smoothedInitialized = false;
    private bool aspectConfigured = false;

    // 화면에 마커 그리기용 (디버그)
    private Point2f markerA, markerB;
    private bool hasMarkersToDraw = false;
    private int markerImageWidth, markerImageHeight;
    private Texture2D dotTexture;

    void Start()
    {
        webcamTexture = new WebCamTexture(requestedWidth, requestedHeight);
        webcamTexture.Play();

        // 미리보기용 RawImage를 직접 안 만들어주셨으면, 코드가 알아서 화면 전체 크기로 만들어줌
        if (previewImage == null)
            previewImage = CreateFullScreenPreview();

        previewImage.texture = webcamTexture;
    }

    private RawImage CreateFullScreenPreview()
    {
        // Canvas 생성
        GameObject canvasGO = new GameObject("WebcamPreviewCanvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -100; // 다른 UI(안내 텍스트 등)보다 뒤에 깔리도록 가장 낮게

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();

        // RawImage를 자식으로 생성, 화면 전체에 꽉 채우기(anchor stretch)
        GameObject imageGO = new GameObject("WebcamPreviewImage");
        imageGO.transform.SetParent(canvasGO.transform, false);
        RawImage img = imageGO.AddComponent<RawImage>();

        RectTransform rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        return img;
    }

    private void ConfigureAspectRatio()
    {
        // RawImage에 AspectRatioFitter를 붙여서, 화면 비율(16:9)이랑 웹캠 비율(보통 4:3 또는 16:9)이
        // 달라도 얼굴이 옆으로 늘어나지 않고 원래 비율 그대로 보이게 함 (남는 공간은 레터박스로 비워둠)
        AspectRatioFitter fitter = previewImage.GetComponent<AspectRatioFitter>();
        if (fitter == null)
            fitter = previewImage.gameObject.AddComponent<AspectRatioFitter>();

        fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        fitter.aspectRatio = (float)webcamTexture.width / webcamTexture.height;
    }

    void Update()
    {
        if (!webcamTexture.isPlaying || !webcamTexture.didUpdateThisFrame)
            return;

        // 웹캠의 실제 해상도가 확정되면(요청한 값과 다를 수 있음) 딱 한 번 비율을 맞춰줌
        if (!aspectConfigured && webcamTexture.width > 16 && previewImage != null)
        {
            ConfigureAspectRatio();
            aspectConfigured = true;
        }

        Texture2DToMat(webcamTexture, ref frameMat);
        if (hsvMat == null) hsvMat = new Mat();
        Cv2.CvtColor(frameMat, hsvMat, ColorConversionCodes.BGR2HSV);

        Quaternion imuRotation = (imuSource != null) ? imuSource.GetRotation() : Quaternion.identity;

        MarkerPairConfig bestPair = null;
        float bestReliability = -1f;
        Point2f bestMidpoint = default;
        float bestPixelDistance = 0f;
        float bestExpectedPerp = 0f;
        Point2f bestCentroidA = default, bestCentroidB = default;

        foreach (var pair in pairs)
        {
            if (!FindTwoLargestBlobs(pair.colorHsvLow, pair.colorHsvHigh, pair.minBlobArea, out Point2f centroidA, out Point2f centroidB))
                continue;

            float pixelDistance = Distance(centroidA, centroidB);
            if (pixelDistance < 2f) continue; // 두 점이 사실상 겹쳐 보이면 신뢰 불가

            // IMU 회전을 이 쌍의 로컬 방향에 적용해서, "지금 이 순간 카메라 기준으로
            // 이 쌍의 벡터가 얼마나 카메라를 정면으로 보고 있는지" 계산
            Vector3 localDir = pair.localDirection.normalized;
            Vector3 worldDir = imuRotation * localDir; // 카메라 좌표계와 IMU가 정렬되어 있다고 가정
            float expectedPerp = new Vector2(worldDir.x, worldDir.y).magnitude; // 카메라 시선(Z)에 수직인 성분

            if (expectedPerp < 0.05f) continue; // 이 쌍은 지금 시선축과 거의 나란함 -> 신뢰 불가

            // 신뢰도 점수: 시선에 수직인 성분이 클수록(=덜 단축돼 보일수록) 신뢰
            float reliability = expectedPerp;

            if (reliability > bestReliability)
            {
                bestReliability = reliability;
                bestPair = pair;
                bestMidpoint = new Point2f((centroidA.X + centroidB.X) / 2f, (centroidA.Y + centroidB.Y) / 2f);
                bestPixelDistance = pixelDistance;
                bestExpectedPerp = expectedPerp;
                bestCentroidA = centroidA;
                bestCentroidB = centroidB;
            }
        }

        hasMarkersToDraw = (bestPair != null);
        if (hasMarkersToDraw)
        {
            markerA = bestCentroidA;
            markerB = bestCentroidB;
            markerImageWidth = frameMat.Width;
            markerImageHeight = frameMat.Height;
        }

        if (bestPair != null)
        {
            activePairName = bestPair.pairName;

            // depth = f * (실제_거리 * 시선수직성분비율) / 픽셀거리
            float expectedPerpMeters = bestPair.realDistanceMeters * bestExpectedPerp;
            float depth = (float)fx * expectedPerpMeters / bestPixelDistance;

            float worldX = (bestMidpoint.X - (float)cx) * depth / (float)fx;
            float worldY = -(bestMidpoint.Y - (float)cy) * depth / (float)fy; // Unity는 Y가 위, 이미지 좌표는 Y가 아래

            Vector3 rawPosition = new Vector3(worldX, worldY, depth);

            if (!smoothedInitialized)
            {
                smoothedPosition = rawPosition;
                smoothedInitialized = true;
            }
            else
            {
                smoothedPosition = Vector3.Lerp(smoothedPosition, rawPosition, smoothingFactor);
            }

            if (targetTransform != null)
                targetTransform.localPosition = smoothedPosition;

            hasValidEstimate = true;
        }
        else
        {
            activePairName = "-";
            hasValidEstimate = false;
            // 아무 쌍도 안 보이면 마지막 값 유지 (아무것도 안 함)
        }
    }

    private bool FindTwoLargestBlobs(Vector3 hsvLow, Vector3 hsvHigh, int minArea, out Point2f centroidA, out Point2f centroidB)
    {
        centroidA = default;
        centroidB = default;
        using (var mask = new Mat())
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(5, 5)))
        {
            Cv2.InRange(hsvMat,
                new Scalar(hsvLow.x, hsvLow.y, hsvLow.z),
                new Scalar(hsvHigh.x, hsvHigh.y, hsvHigh.z),
                mask);

            // 노이즈 제거: 작은 티끌은 지우고(Open), 점 하나가 여러 조각으로 쪼개진 건 이어붙임(Close)
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel, iterations: 1);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel, iterations: 2);

            Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length < 2) return false; // 같은 색 점이 최소 2개는 보여야 쌍으로 인정

            // 면적 기준으로 정렬해서 가장 큰 2개만 뽑음
            double bestArea = -1, secondArea = -1;
            Point2f bestCenter = default, secondCenter = default;

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < minArea) continue;

                var m = Cv2.Moments(contour);
                if (m.M00 == 0) continue;
                var center = new Point2f((float)(m.M10 / m.M00), (float)(m.M01 / m.M00));

                if (area > bestArea)
                {
                    secondArea = bestArea; secondCenter = bestCenter;
                    bestArea = area; bestCenter = center;
                }
                else if (area > secondArea)
                {
                    secondArea = area; secondCenter = center;
                }
            }

            if (bestArea < 0 || secondArea < 0) return false; // 유효한 blob이 2개 미만

            centroidA = bestCenter;
            centroidB = secondCenter;
            return true;
        }
    }

    private float Distance(Point2f a, Point2f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private void Texture2DToMat(WebCamTexture tex, ref Mat mat)
    {
        Color32[] pixels = tex.GetPixels32();

        var bytes = new byte[pixels.Length * 4];
        for (int y = 0; y < tex.height; y++)
        {
            int srcRow = tex.height - 1 - y;
            for (int x = 0; x < tex.width; x++)
            {
                Color32 c = pixels[srcRow * tex.width + x];
                int idx = (y * tex.width + x) * 4;
                bytes[idx + 0] = c.b;
                bytes[idx + 1] = c.g;
                bytes[idx + 2] = c.r;
                bytes[idx + 3] = c.a;
            }
        }

        if (mat == null || mat.Width != tex.width || mat.Height != tex.height)
        {
            mat?.Dispose();
            mat = new Mat(tex.height, tex.width, MatType.CV_8UC4);
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
    }

    void OnDestroy()
    {
        if (webcamTexture != null) webcamTexture.Stop();
        frameMat?.Dispose();
        hsvMat?.Dispose();
        if (dotTexture != null) Destroy(dotTexture);
    }

    void OnGUI()
    {
        if (showDebugWindow)
        {
            string webcamStatus = (webcamTexture != null && webcamTexture.isPlaying)
                ? $"웹캠: {webcamTexture.width}x{webcamTexture.height} (재생중)"
                : "웹캠: 아직 재생 안 됨";
            GUI.Label(new UnityEngine.Rect(10, 10, 400, 20), webcamStatus);

            GUI.Label(new UnityEngine.Rect(10, 30, 400, 20), $"Active pair: {activePairName}  (valid: {hasValidEstimate})");
            if (targetTransform != null)
                GUI.Label(new UnityEngine.Rect(10, 50, 400, 20), $"Position: {targetTransform.localPosition}");

            DrawMarkersOnPreview();
        }
    }

    private void DrawMarkersOnPreview()
    {
        if (!hasMarkersToDraw || previewImage == null || markerImageWidth <= 0) return;

        // previewImage(RawImage)가 화면상에서 실제로 그려지는 영역(픽셀 좌표)을 구함
        Vector3[] corners = new Vector3[4];
        previewImage.rectTransform.GetWorldCorners(corners); // 0:좌하 1:좌상 2:우상 3:우하

        Canvas canvas = previewImage.canvas;
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        // Unity Screen 좌표(원점 좌하단) -> GUI 좌표(원점 좌상단)로 변환
        float guiLeft = bottomLeft.x;
        float guiRight = topRight.x;
        float guiTop = Screen.height - topRight.y;
        float guiBottom = Screen.height - bottomLeft.y;
        float guiWidth = guiRight - guiLeft;
        float guiHeight = guiBottom - guiTop;

        if (dotTexture == null)
        {
            dotTexture = new Texture2D(1, 1);
            dotTexture.SetPixel(0, 0, Color.white);
            dotTexture.Apply();
        }

        DrawDotMarker(markerA, guiLeft, guiTop, guiWidth, guiHeight, Color.red);
        DrawDotMarker(markerB, guiLeft, guiTop, guiWidth, guiHeight, Color.green);

        // 두 점을 잇는 선도 그려서 "쌍"이라는 걸 시각적으로 확인 가능하게 함
        Vector2 screenA = MarkerToGuiPoint(markerA, guiLeft, guiTop, guiWidth, guiHeight);
        Vector2 screenB = MarkerToGuiPoint(markerB, guiLeft, guiTop, guiWidth, guiHeight);
        DrawGuiLine(screenA, screenB, Color.yellow, 2f);
    }

    private Vector2 MarkerToGuiPoint(Point2f marker, float guiLeft, float guiTop, float guiWidth, float guiHeight)
    {
        float normX = marker.X / markerImageWidth;
        float normY = marker.Y / markerImageHeight; // 이미 위쪽이 0인 이미지 좌표라 GUI 좌표랑 방향이 같음
        return new Vector2(guiLeft + normX * guiWidth, guiTop + normY * guiHeight);
    }

    private void DrawDotMarker(Point2f marker, float guiLeft, float guiTop, float guiWidth, float guiHeight, Color color)
    {
        Vector2 p = MarkerToGuiPoint(marker, guiLeft, guiTop, guiWidth, guiHeight);
        float size = 14f;
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new UnityEngine.Rect(p.x - size / 2f, p.y - size / 2f, size, size), dotTexture);
        GUI.color = prev;
    }

    private void DrawGuiLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Color prev = GUI.color;
        GUI.color = color;
        float length = Vector2.Distance(a, b);
        float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

        Matrix4x4 matrixBackup = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new UnityEngine.Rect(a.x, a.y - width / 2f, length, width), dotTexture);
        GUI.matrix = matrixBackup;

        GUI.color = prev;
    }
}

// IMU(MPU6050) 스크립트가 준비되면 이 인터페이스를 구현해서 imuSource에 연결하면 됩니다.
// 아직 없으면 imuSource를 비워둬도 동작합니다 (Quaternion.identity로 대체).
public interface IImuSource
{
    Quaternion GetRotation();
}
