using UnityEngine;

/// <summary>
/// 왼손(표현하는 손)의 상태를 계산한다.
/// - 높이: 크레센도(올리기) / 디크레센도(내리기) 판정용
/// - 위치 구역 + 들어올림: 큐(파트 가리키기) 판정용
/// - 쥠(주먹 또는 엄지·검지 붙이기): 컷오프 판정용
/// </summary>
public class LeftHandGestures : MonoBehaviour
{
    public enum Zone { Left = 0, Center = 1, Right = 2 }

    [Header("연결")]
    [SerializeField] private HandTracker tracker;
    [Tooltip("켜면 오른손으로 표현 동작 (왼손잡이용, ConductorInput의 설정과 반대로)")]
    [SerializeField] private bool useRightHand = false;

    [Header("보정")]
    [Range(0.05f, 1f)]
    [SerializeField] private float smoothing = 0.35f;

    [Header("큐(가리키기)")]
    [SerializeField] private float raisedHeight = 0.5f;      // 손바닥 중심이 화면 이 높이보다 위면 "들어올림"
    [SerializeField] private float leftZoneEdge = 0.38f;     // 이보다 왼쪽 = 왼쪽 구역
    [SerializeField] private float rightZoneEdge = 0.62f;    // 이보다 오른쪽 = 오른쪽 구역

    [Header("컷오프(쥐기)")]
    [SerializeField] private float curlRatio = 1.05f;        // 손끝이 손가락 마디보다 손목에 가까우면 접힌 손가락
    [SerializeField] private float pinchRatio = 0.35f;       // 엄지·검지 끝 거리 / 손 크기 가 이보다 작으면 집기

    [Header("화면 표시")]
    [SerializeField] private bool showDebugInfo = true;

    // ───── 다른 스크립트에서 읽을 값 ─────
    public bool HasHand { get; private set; }
    public Vector2 Position { get; private set; }            // 손바닥 중심 (0~1, 왼쪽 아래 원점)
    public float Height => Position.y;
    public Zone CurrentZone { get; private set; } = Zone.Center;
    public bool IsRaised { get; private set; }
    public bool IsFist { get; private set; }
    public bool IsPinch { get; private set; }
    public bool IsClosed => IsFist || IsPinch;

    private int lastVersion = -1;
    private bool hasSmooth;

    // 손바닥 중심을 계산할 관절: 손목, 검지·중지·약지·새끼 시작 마디
    private static readonly int[] PalmPoints = { 0, 5, 9, 13, 17 };

    private void Update()
    {
        if (tracker == null || tracker.ResultVersion == lastVersion) return;
        lastVersion = tracker.ResultVersion;

        int hand = useRightHand ? tracker.RightIndex : tracker.LeftIndex;
        HasHand = hand >= 0;
        if (!HasHand)
        {
            hasSmooth = false;
            IsFist = IsPinch = IsRaised = false;
            return;
        }

        // 손바닥 중심
        Vector2 palm = Vector2.zero;
        foreach (int i in PalmPoints) palm += tracker.Points[hand, i];
        palm /= PalmPoints.Length;

        Position = hasSmooth ? Vector2.Lerp(Position, palm, smoothing) : palm;
        hasSmooth = true;

        CurrentZone = Position.x < leftZoneEdge ? Zone.Left
                    : Position.x > rightZoneEdge ? Zone.Right
                    : Zone.Center;
        IsRaised = Position.y > raisedHeight;

        // 주먹: 검지·중지·약지·새끼 중 3개 이상 접힘
        int curled = 0;
        if (IsCurled(hand, 8, 6)) curled++;
        if (IsCurled(hand, 12, 10)) curled++;
        if (IsCurled(hand, 16, 14)) curled++;
        if (IsCurled(hand, 20, 18)) curled++;
        IsFist = curled >= 3;

        // 집기: 엄지 끝과 검지 끝이 손 크기에 비해 아주 가까움
        float handSize = Dist(hand, 0, 9);                    // 손목 ~ 중지 시작 마디
        IsPinch = handSize > 0.0001f && Dist(hand, 4, 8) / handSize < pinchRatio;
    }

    private bool IsCurled(int hand, int tip, int pip)
    {
        return Dist(hand, 0, tip) < Dist(hand, 0, pip) * curlRatio;
    }

    /// <summary>두 관절 사이 거리 (좌우를 화면 비율로 보정)</summary>
    private float Dist(int hand, int a, int b)
    {
        Vector2 d = tracker.Points[hand, a] - tracker.Points[hand, b];
        d.x *= tracker.ImageAspect;
        return d.magnitude;
    }

    private GUIStyle style;

    private void OnGUI()
    {
        if (!showDebugInfo) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };

        string text = HasHand
            ? $"왼손: 높이 {Height:0.00} | 구역 {ZoneName(CurrentZone)} | 들어올림 {(IsRaised ? "O" : "X")} | 쥠 {(IsClosed ? "O" : "X")}"
            : "왼손: 인식 안 됨";
        GUI.Label(new Rect(20, Screen.height - 50, 1000, 34), text, style);
    }

    public static string ZoneName(Zone z) => z switch
    {
        Zone.Left => "왼쪽",
        Zone.Right => "오른쪽",
        _ => "가운데",
    };
}