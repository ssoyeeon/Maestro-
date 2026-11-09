using System.IO;
using UnityEngine;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.HandLandmarker;
using MpImage = Mediapipe.Image;          // UnityEngine.UI.Image와 이름 충돌 방지
using UiImage = UnityEngine.UI.Image;
using Rect = UnityEngine.Rect;            // Mediapipe.Rect와 충돌 방지
using Color = UnityEngine.Color;          // Mediapipe.Color와 충돌 방지

/// <summary>
/// WebcamInput의 영상을 MediaPipe Hand Landmarker에 넘겨 손 관절 21개를 인식하고,
/// 왼손/오른손을 구분해 웹캠 화면(RawImage) 위에 점으로 표시한다.
/// </summary>
public class HandTracker : MonoBehaviour
{
    [Header("연결")]
    [SerializeField] private WebcamInput webcam;
    [SerializeField] private RectTransform display;        // WebcamInput이 쓰는 RawImage의 RectTransform

    [Header("인식 설정")]
    [SerializeField] private string modelFileName = "hand_landmarker.bytes"; // StreamingAssets 안의 파일 이름
    [SerializeField] private int numHands = 2;              // 지휘(오른손) + 표현(왼손)
    [Range(0.1f, 0.9f)]
    [SerializeField] private float minDetectionConfidence = 0.3f;
    [Range(0.1f, 0.9f)]
    [SerializeField] private float minPresenceConfidence = 0.3f;
    [Range(0.1f, 0.9f)]
    [SerializeField] private float minTrackingConfidence = 0.3f;
    [SerializeField] private bool mirror = true;            // WebcamInput의 mirror 설정과 맞출 것

    [Header("왼손/오른손 구분")]
    [Tooltip("MediaPipe는 거울 영상 기준으로 왼손/오른손을 판단한다. 실제 손과 반대로 나오면 이 값을 바꿀 것.")]
    [SerializeField] private bool swapHandedness = false;

    [Header("표시")]
    [SerializeField] private float dotSize = 12f;
    [SerializeField] private Color rightHandColor = new Color(1f, 0.3f, 0.3f);  // 오른손 빨강
    [SerializeField] private Color leftHandColor = new Color(0.3f, 0.7f, 1f);   // 왼손 파랑

    public const int MaxHands = 2;
    public const int LandmarkCount = 21;

    /// <summary>인식된 손 개수 (0~2)</summary>
    public int HandCount { get; private set; }

    /// <summary>오른손/왼손이 Points의 몇 번 손인지. 없으면 -1.</summary>
    public int RightIndex { get; private set; } = -1;
    public int LeftIndex { get; private set; } = -1;

    /// <summary>새 인식 결과가 나올 때마다 1씩 증가.</summary>
    public int ResultVersion { get; private set; }

    /// <summary>마지막 인식 결과가 나온 시각 (Time.time 기준)</summary>
    public float ResultTime { get; private set; }

    /// <summary>웹캠 영상의 가로/세로 비율.</summary>
    public float ImageAspect { get; private set; } = 4f / 3f;

    /// <summary>웹캠 화면 RawImage.</summary>
    public RectTransform Display => display;

    /// <summary>
    /// 게임 로직용 좌표. [손 번호, 관절 번호], 0~1 정규화, 왼쪽 아래가 (0,0).
    /// mirror가 켜져 있으면 화면에 보이는 방향(플레이어 기준 왼쪽 = 화면 왼쪽)이다.
    /// 관절 번호: 0 = 손목, 4 = 엄지 끝, 8 = 검지 끝, 12 = 중지 끝, 16 = 약지 끝, 20 = 새끼 끝
    /// </summary>
    public readonly Vector2[,] Points = new Vector2[MaxHands, LandmarkCount];

    /// <summary>Points 좌표를 Display 안의 로컬 위치로 변환. Display의 자식 UI 배치용.</summary>
    public Vector2 PointToDisplayLocal(Vector2 p)
    {
        float rawX = mirror ? 1f - p.x : p.x;
        Rect r = display.rect;
        return new Vector2((rawX - 0.5f) * r.width, (p.y - 0.5f) * r.height);
    }

    private HandLandmarker landmarker;
    private Texture2D frameTex;
    private Color32[] srcPixels, flippedPixels;
    private RectTransform[,] dots;
    private UiImage[,] dotImages;
    private long lastTimestamp;
    private readonly bool[] isRightLabel = new bool[MaxHands];

    private void Start()
    {
        string modelPath = Path.Combine(Application.streamingAssetsPath, modelFileName);
        if (!File.Exists(modelPath))
        {
            Debug.LogError($"모델 파일이 없습니다: {modelPath}");
            return;
        }

        byte[] modelBytes = File.ReadAllBytes(modelPath);
        var baseOptions = new BaseOptions(BaseOptions.Delegate.CPU, modelAssetBuffer: modelBytes);
        var options = new HandLandmarkerOptions(
            baseOptions,
            runningMode: RunningMode.VIDEO,
            numHands: numHands,
            minHandDetectionConfidence: minDetectionConfidence,
            minHandPresenceConfidence: minPresenceConfidence,
            minTrackingConfidence: minTrackingConfidence);
        landmarker = HandLandmarker.CreateFromOptions(options);

        CreateDots();
        Debug.Log("HandLandmarker 준비 완료");
    }

    private void Update()
    {
        if (landmarker == null || webcam == null || !webcam.IsReady) return;

        WebCamTexture cam = webcam.CamTexture;
        if (!cam.didUpdateThisFrame) return;

        int w = cam.width, h = cam.height;
        EnsureBuffers(w, h);
        ImageAspect = (float)w / h;

        // Unity 텍스처는 아래쪽 행부터, MediaPipe는 위쪽 행부터 읽으므로 상하 반전
        cam.GetPixels32(srcPixels);
        for (int y = 0; y < h; y++)
            System.Array.Copy(srcPixels, y * w, flippedPixels, (h - 1 - y) * w, w);
        frameTex.SetPixels32(flippedPixels);
        frameTex.Apply(false);

        long ts = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        if (ts <= lastTimestamp) ts = lastTimestamp + 1;
        lastTimestamp = ts;

        using var image = new MpImage(ImageFormat.Types.Format.Srgba, frameTex);
        HandLandmarkerResult result = landmarker.DetectForVideo(image, ts);

        ApplyResult(result);
    }

    private void ApplyResult(HandLandmarkerResult result)
    {
        HandCount = result.handLandmarks == null ? 0 : Mathf.Min(result.handLandmarks.Count, MaxHands);
        Rect rect = display.rect;

        for (int hand = 0; hand < MaxHands; hand++)
        {
            bool visible = hand < HandCount;
            for (int i = 0; i < LandmarkCount; i++)
            {
                dots[hand, i].gameObject.SetActive(visible);
                if (!visible) continue;

                var lm = result.handLandmarks[hand].landmarks[i];   // 0~1, 왼쪽 위가 (0,0)
                dots[hand, i].anchoredPosition = new Vector2((lm.x - 0.5f) * rect.width, (0.5f - lm.y) * rect.height);

                float gx = mirror ? 1f - lm.x : lm.x;
                Points[hand, i] = new Vector2(gx, 1f - lm.y);
            }

            if (visible) isRightLabel[hand] = ReadIsRight(result, hand);
        }

        AssignHands();

        // 손 색칠: 오른손 빨강, 왼손 파랑
        for (int hand = 0; hand < HandCount; hand++)
        {
            Color c = hand == RightIndex ? rightHandColor : leftHandColor;
            for (int i = 0; i < LandmarkCount; i++) dotImages[hand, i].color = c;
        }

        ResultTime = Time.time;
        ResultVersion++;
    }

    /// <summary>MediaPipe가 알려준 손 라벨로 오른손인지 판단. 라벨이 없으면 화면 위치로 추정.</summary>
    private bool ReadIsRight(HandLandmarkerResult result, int hand)
    {
        string label = null;
        if (result.handedness != null && hand < result.handedness.Count)
        {
            var cats = result.handedness[hand].categories;
            if (cats != null && cats.Count > 0) label = cats[0].categoryName;
        }

        if (string.IsNullOrEmpty(label))
            return Points[hand, 0].x > 0.5f;    // 라벨이 없으면 화면 오른쪽에 있는 손을 오른손으로

        bool isRight = label == "Right";
        return swapHandedness ? !isRight : isRight;
    }

    private void AssignHands()
    {
        RightIndex = LeftIndex = -1;
        if (HandCount == 1)
        {
            if (isRightLabel[0]) RightIndex = 0; else LeftIndex = 0;
        }
        else if (HandCount == 2)
        {
            if (isRightLabel[0] != isRightLabel[1])
            {
                RightIndex = isRightLabel[0] ? 0 : 1;
            }
            else
            {
                // 둘 다 같은 손으로 판단되면 화면에서 더 오른쪽에 있는 손을 오른손으로
                RightIndex = Points[0, 0].x > Points[1, 0].x ? 0 : 1;
            }
            LeftIndex = 1 - RightIndex;
        }
    }

    private void EnsureBuffers(int w, int h)
    {
        if (frameTex != null && frameTex.width == w && frameTex.height == h) return;
        if (frameTex != null) Destroy(frameTex);
        frameTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        srcPixels = new Color32[w * h];
        flippedPixels = new Color32[w * h];
    }

    private void CreateDots()
    {
        dots = new RectTransform[MaxHands, LandmarkCount];
        dotImages = new UiImage[MaxHands, LandmarkCount];

        for (int hand = 0; hand < MaxHands; hand++)
        {
            for (int i = 0; i < LandmarkCount; i++)
            {
                var go = new GameObject($"Hand{hand}_Point{i}", typeof(RectTransform), typeof(UiImage));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(display, false);
                rt.sizeDelta = Vector2.one * dotSize;
                if (i == 0 || i == 8) rt.sizeDelta *= 1.8f;   // 손목, 검지 끝 강조
                var img = go.GetComponent<UiImage>();
                img.raycastTarget = false;
                go.SetActive(false);
                dots[hand, i] = rt;
                dotImages[hand, i] = img;
            }
        }
    }

    private void OnDestroy()
    {
        landmarker?.Close();
        if (frameTex != null) Destroy(frameTex);
    }
}