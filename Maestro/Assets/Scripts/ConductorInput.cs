using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 3단계 (실제 지휘 버전): 손의 2차원 움직임에서 박(이크투스)을 찾고,
/// 1박(다운비트)을 구분해 박자(2/4, 3/4, 4/4)와 BPM, 세기를 계산한다.
///
/// 원리: 지휘 도형의 모든 박은 손이 "아래쪽 성분을 가진 채 빠르게 들어와서"
///       튕기거나, 급하게 꺾이거나, 멈추는 지점에서 찍힌다.
///       그중 가장 크게 수직으로 떨어지는 박이 1박이다.
/// </summary>
public class ConductorInput : MonoBehaviour
{
    [Header("연결")]
    [SerializeField] private HandTracker tracker;

    [Header("추적할 손 / 관절")]
    [Tooltip("켜면 왼손으로 박을 지휘 (왼손잡이용)")]
    [SerializeField] private bool conductWithLeftHand = false;
    [SerializeField] private int landmarkIndex = 0;          // 0 = 손목(추천), 8 = 검지 끝

    [Header("박 검출")]
    [Range(0.05f, 1f)]
    [SerializeField] private float smoothing = 0.6f;         // 높을수록 반응 빠름, 낮을수록 떨림 적음
    [SerializeField] private float armSpeed = 0.5f;          // 이 속도(화면 높이/초) 이상으로 들어와야 박 후보
    [Range(0f, 1f)]
    [SerializeField] private float minDownRatio = 0.2f;      // 들어오는 방향에 아래쪽 성분이 이만큼은 있어야 함
    [Range(30f, 150f)]
    [SerializeField] private float turnAngle = 70f;          // 이 각도 이상 꺾이면 박
    [Range(0.1f, 0.8f)]
    [SerializeField] private float stopRatio = 0.35f;        // 최고 속도의 이 비율 아래로 급감속하면 박
    [SerializeField] private float minBeatInterval = 0.22f;
    [SerializeField] private float maxBeatInterval = 2.0f;
    [SerializeField] private int bpmAverageCount = 4;

    [Header("1박(다운비트) 판정")]
    [SerializeField] private float minDownbeatDrop = 0.08f;  // 1박은 최소 이만큼(화면 높이 비율) 떨어져야 함
    [SerializeField] private float downbeatRatio = 1.4f;     // 다른 박들 평균 낙차보다 이 배수 이상 크면 1박

    [Header("세기")]
    [SerializeField] private float minGestureSize = 0.05f;   // 한 박 도형 크기가 이보다 작으면 가장 여림(0)
    [SerializeField] private float maxGestureSize = 0.45f;   // 이보다 크면 가장 셈(1)

    [Header("손 놓침 / 지휘 중단")]
    [SerializeField] private float lostGrace = 0.3f;
    [SerializeField] private float stopAfterSeconds = 2.0f;

    [Header("화면 표시")]
    [SerializeField] private bool showDebugInfo = true;      // 왼쪽 위 진단 정보
    [SerializeField] private bool showTrail = true;
    [SerializeField] private int trailLength = 40;
    [SerializeField] private float markerLifetime = 1.5f;

    // ───── 다른 스크립트에서 읽을 값 ─────
    public float Bpm { get; private set; }
    public float Intensity { get; private set; } = 0.5f;    // 0 = 여리게, 1 = 세게
    public bool IsConducting { get; private set; }
    public int BeatInMeasure { get; private set; }           // 지금 몇 박째인지 (1부터)
    public int Meter { get; private set; }                   // 박자 (2, 3, 4...). 0이면 아직 모름
    /// <summary>박이 찍힐 때마다 호출. 인자는 마디 안에서 몇 박째인지 (1 = 다운비트)</summary>
    public event Action<int> OnBeat;

    // 내부 상태
    private int lastVersion = -1;
    private bool hasPrev;
    private Vector2 smoothPos, prevPos, prevVel, vel;
    private float lastSampleTime, lastSeenTime;

    private bool armed;
    private Vector2 approachDir;            // 박으로 들어오는 방향
    private float peakSpeed;
    private float armedTime;

    private float lastBeatTime = -999f;
    private readonly Queue<float> intervals = new Queue<float>();
    private readonly List<float> recentDrops = new List<float>();
    private float maxYSinceBeat;
    private Vector2 boxMin, boxMax;          // 한 박 동안 그린 도형의 범위
    private int beatsSinceDownbeat;
    private int lastMeasureLength;

    // 진단용
    private int resultCount;
    private float fpsTimer, detectFps, beatFlash;
    private string lastTrigger = "-";

    // 표시용
    private readonly List<Image> trailDots = new List<Image>();
    private readonly Queue<Vector2> trailPoints = new Queue<Vector2>();
    private readonly List<(Image img, float time)> markers = new List<(Image, float)>();
    private static readonly Color[] BeatColors =
    {
        new Color(1f, 0.25f, 0.25f),   // 1박 빨강
        new Color(1f, 0.6f, 0.15f),    // 2박 주황
        new Color(0.3f, 0.9f, 0.35f),  // 3박 초록
        new Color(0.3f, 0.6f, 1f),     // 4박 파랑
        new Color(0.8f, 0.5f, 1f),     // 5박 이상 보라
    };

    // ───────────────────────────────────────────────

    private void Start()
    {
        if (tracker != null && tracker.Display != null) CreateTrail();
    }

    private void Update()
    {
        UpdateFps();
        beatFlash = Mathf.Max(0f, beatFlash - Time.deltaTime * 4f);
        UpdateMarkers();

        if (IsConducting && Time.time - lastBeatTime > stopAfterSeconds)
        {
            IsConducting = false;
            intervals.Clear();
            BeatInMeasure = 0;
            beatsSinceDownbeat = 0;
        }

        if (tracker == null || tracker.ResultVersion == lastVersion) return;
        lastVersion = tracker.ResultVersion;
        resultCount++;

        int handIndex = conductWithLeftHand ? tracker.LeftIndex : tracker.RightIndex;
        if (handIndex < 0)
        {
            if (tracker.ResultTime - lastSeenTime > lostGrace)
            {
                hasPrev = false;
                armed = false;
            }
            return;
        }
        lastSeenTime = tracker.ResultTime;

        ProcessSample(tracker.Points[handIndex, landmarkIndex], tracker.ResultTime);
    }

    private void ProcessSample(Vector2 raw, float now)
    {
        if (!hasPrev)
        {
            smoothPos = prevPos = raw;
            vel = prevVel = Vector2.zero;
            lastSampleTime = now;
            ResetGestureBox();
            hasPrev = true;
            return;
        }

        float dt = Mathf.Max(now - lastSampleTime, 0.001f);
        lastSampleTime = now;

        smoothPos = Vector2.Lerp(smoothPos, raw, smoothing);

        // 좌우는 화면 비율을 곱해서 위아래와 같은 단위(화면 높이)로 맞춘다
        Vector2 d = smoothPos - prevPos;
        d.x *= tracker.ImageAspect;
        vel = d / dt;
        float speed = vel.magnitude;

        // 도형 크기, 낙차 기록
        boxMin = Vector2.Min(boxMin, smoothPos);
        boxMax = Vector2.Max(boxMax, smoothPos);
        maxYSinceBeat = Mathf.Max(maxYSinceBeat, smoothPos.y);

        if (!armed)
        {
            // 아래쪽 성분을 가진 채 빠르게 움직이기 시작하면 박 후보로 "장전"
            if (speed > armSpeed && vel.y < -minDownRatio * speed)
            {
                armed = true;
                approachDir = vel / speed;
                peakSpeed = speed;
                armedTime = now;
            }
        }
        else
        {
            // 들어오는 방향은 빠르게 내려오는 동안 계속 갱신
            if (speed > armSpeed * 0.7f && vel.y < 0f) approachDir = vel / speed;
            peakSpeed = Mathf.Max(peakSpeed, speed);

            bool bounce = prevVel.y < 0f && vel.y >= 0f;                          // 위로 튕김
            bool turn = speed > armSpeed * 0.3f && Vector2.Angle(approachDir, vel) > turnAngle; // 급격한 꺾임
            bool stop = speed < peakSpeed * stopRatio;                            // 급정거

            if (bounce || turn || stop)
            {
                armed = false;
                lastTrigger = bounce ? "튕김" : turn ? "꺾임" : "정지";
                RegisterBeat(now);
            }
            else if (now - armedTime > maxBeatInterval)
            {
                armed = false;                                                    // 너무 오래 장전 상태면 취소
            }
        }

        prevPos = smoothPos;
        prevVel = vel;
        AddTrailPoint(smoothPos);
    }

    private void RegisterBeat(float now)
    {
        float interval = now - lastBeatTime;
        if (interval < minBeatInterval) return;

        // ── 1박(다운비트)인지 판정 ──
        float drop = maxYSinceBeat - smoothPos.y;
        bool mostlyVertical = Mathf.Abs(approachDir.x) < Mathf.Abs(approachDir.y) * 0.8f;
        float avgDrop = 0f;
        foreach (float v in recentDrops) avgDrop += v;
        avgDrop = recentDrops.Count > 0 ? avgDrop / recentDrops.Count : 0f;

        bool isDownbeat = mostlyVertical && drop > minDownbeatDrop &&
                          (recentDrops.Count < 2 || drop >= avgDrop * downbeatRatio);

        recentDrops.Add(drop);
        if (recentDrops.Count > 6) recentDrops.RemoveAt(0);

        // ── 마디 안 박 번호, 박자 ──
        if (isDownbeat)
        {
            int measureLength = beatsSinceDownbeat;
            if (measureLength >= 2 && measureLength <= 6)
            {
                // 같은 길이의 마디가 두 번 연속 나오면 박자로 확정
                if (measureLength == lastMeasureLength) Meter = measureLength;
                lastMeasureLength = measureLength;
            }
            beatsSinceDownbeat = 1;
        }
        else
        {
            beatsSinceDownbeat++;
        }
        BeatInMeasure = beatsSinceDownbeat;

        // ── BPM ──
        if (interval <= maxBeatInterval)
        {
            intervals.Enqueue(interval);
            while (intervals.Count > bpmAverageCount) intervals.Dequeue();
            float sum = 0f;
            foreach (float v in intervals) sum += v;
            Bpm = 60f / (sum / intervals.Count);
            IsConducting = true;
        }

        // ── 세기: 한 박 동안 그린 도형의 크기 ──
        Vector2 box = boxMax - boxMin;
        box.x *= tracker.ImageAspect;
        float target = Mathf.InverseLerp(minGestureSize, maxGestureSize, box.magnitude);
        Intensity = Mathf.Lerp(Intensity, target, 0.5f);

        maxYSinceBeat = smoothPos.y;
        ResetGestureBox();
        lastBeatTime = now;
        beatFlash = 1f;

        SpawnMarker(smoothPos, BeatInMeasure);
        OnBeat?.Invoke(BeatInMeasure);
    }

    private void ResetGestureBox()
    {
        boxMin = boxMax = smoothPos;
    }

    private void UpdateFps()
    {
        fpsTimer += Time.unscaledDeltaTime;
        if (fpsTimer >= 1f)
        {
            detectFps = resultCount / fpsTimer;
            resultCount = 0;
            fpsTimer = 0f;
        }
    }

    // ───── 궤적 / 박 표시 ─────

    private void CreateTrail()
    {
        for (int i = 0; i < trailLength; i++)
        {
            var img = CreateDot($"Trail{i}", 8f);
            img.gameObject.SetActive(false);
            trailDots.Add(img);
        }
    }

    private void AddTrailPoint(Vector2 p)
    {
        if (!showTrail || trailDots.Count == 0) return;
        trailPoints.Enqueue(p);
        while (trailPoints.Count > trailDots.Count) trailPoints.Dequeue();

        int i = 0;
        int count = trailPoints.Count;
        foreach (Vector2 tp in trailPoints)
        {
            var img = trailDots[i];
            img.gameObject.SetActive(true);
            img.rectTransform.anchoredPosition = tracker.PointToDisplayLocal(tp);
            img.color = new Color(1f, 1f, 1f, (float)(i + 1) / count * 0.8f); // 오래된 점일수록 흐리게
            i++;
        }
    }

    private void SpawnMarker(Vector2 p, int beatNumber)
    {
        if (tracker == null || tracker.Display == null) return;

        // 오래된 마커 재활용
        Image img = null;
        for (int i = 0; i < markers.Count; i++)
        {
            if (Time.time - markers[i].time > markerLifetime)
            {
                img = markers[i].img;
                markers.RemoveAt(i);
                break;
            }
        }
        if (img == null) img = CreateDot("BeatMarker", 1f);

        float size = beatNumber == 1 ? 46f : 30f;      // 1박은 크게
        img.rectTransform.sizeDelta = Vector2.one * size;
        img.rectTransform.anchoredPosition = tracker.PointToDisplayLocal(p);
        img.rectTransform.SetAsLastSibling();
        img.color = BeatColors[Mathf.Clamp(beatNumber - 1, 0, BeatColors.Length - 1)];
        img.gameObject.SetActive(true);
        markers.Add((img, Time.time));
    }

    private void UpdateMarkers()
    {
        foreach (var (img, time) in markers)
        {
            float age = Time.time - time;
            if (age > markerLifetime) { img.gameObject.SetActive(false); continue; }
            Color c = img.color;
            c.a = 1f - age / markerLifetime;
            img.color = c;
        }
    }

    private Image CreateDot(string name, float size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(tracker.Display, false);
        rt.sizeDelta = Vector2.one * size;
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    // ───── 화면 정보 (확인용) ─────
    private GUIStyle style, bigStyle;

    private void OnGUI()
    {
        if (!showDebugInfo) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
        bigStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold };

        GUI.color = Color.white;
        string meterText = Meter > 0 ? $"{Meter}/4박자" : "박자 분석 중";
        GUI.Label(new Rect(20, 20, 700, 34), IsConducting ? $"BPM: {Bpm:0}  |  {meterText}" : "지휘 대기 중", style);
        GUI.Label(new Rect(20, 54, 700, 34), $"세기: {Intensity:0.00}", style);

        // 현재 박 번호를 크게, 박 색으로
        if (IsConducting && BeatInMeasure > 0)
        {
            Color c = BeatColors[Mathf.Clamp(BeatInMeasure - 1, 0, BeatColors.Length - 1)];
            c.a = 0.4f + 0.6f * beatFlash;
            GUI.color = c;
            GUI.Label(new Rect(20, 88, 200, 80), BeatInMeasure.ToString(), bigStyle);
            GUI.color = Color.white;
        }

        int hands = tracker != null ? tracker.HandCount : 0;
        GUI.Label(new Rect(20, 170, 900, 34), $"손: {hands}개 | 초당 인식: {detectFps:0}회", style);
        GUI.Label(new Rect(20, 204, 900, 34),
            $"속도: {vel.magnitude:0.00} (기준 {armSpeed:0.00}) | 장전: {(armed ? "O" : "X")} | 마지막 박: {lastTrigger}", style);
    }
}