using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 게임 진행: 곡을 재생하고, 지휘 박의 타이밍과 왼손 미션을 판정해 점수를 쌓는다.
/// 박 지도(탭 기록)를 쓰므로 박자·템포가 바뀌는 곡도 녹음과 정확히 맞는다.
/// </summary>
public class RhythmGame : MonoBehaviour
{
    [Header("연결")]
    [SerializeField] private SongChart chart;
    [SerializeField] private ConductorInput conductor;
    [SerializeField] private LeftHandGestures leftHand;
    [SerializeField] private AudioSource audioSource;

    [Header("박 판정 범위 (초)")]
    [SerializeField] private float perfectWindow = 0.07f;
    [SerializeField] private float greatWindow = 0.13f;
    [SerializeField] private float goodWindow = 0.22f;

    [Header("지연 보정")]
    [Tooltip("웹캠 촬영 + 인식에 걸리는 시간(초). 판정이 항상 '느림'이면 올리고, '빠름'이면 내린다.")]
    [SerializeField] private float inputLatency = 0.12f;
    [SerializeField] private float startDelay = 1.0f;

    [Header("왼손 미션 판정")]
    [SerializeField] private float dynamicsChange = 0.15f;
    [SerializeField] private float holdTime = 0.15f;

    [Header("점수")]
    [SerializeField] private int perfectScore = 300;
    [SerializeField] private int greatScore = 200;
    [SerializeField] private int goodScore = 100;
    [SerializeField] private int downbeatBonus = 50;
    [SerializeField] private int eventScore = 500;

    private class EventState
    {
        public ChartEvent ev;
        public int beatIndex;
        public double judgeStart, judgeEnd;
        public bool active, done, success;
        public float startHeight = -1f, minH, maxH, held;
    }

    private List<SongChart.BeatInfo> beats;
    private bool[] judged;
    private int nextMissCheck;
    private readonly List<EventState> events = new List<EventState>();

    private bool playing, finished;
    private double startDsp, startOffset, endTime;
    private float baseVolume = 1f;

    private int score, combo, maxCombo, perfect, great, good, miss, eventSuccess, eventFail;
    private string feedback = "", eventFeedback = "";
    private Color feedbackColor = Color.white, eventFeedbackColor = Color.white;
    private float feedbackTime = -99f, eventFeedbackTime = -99f;

    /// <summary>현재 음원 재생 위치(초)</summary>
    private double SongTime => AudioSettings.dspTime - startDsp + startOffset;

    private void OnEnable() { if (conductor != null) conductor.OnBeat += HandleBeat; }
    private void OnDisable() { if (conductor != null) conductor.OnBeat -= HandleBeat; }

    // ───────────── 시작 ─────────────

    public void StartSong()
    {
        if (chart == null || chart.audio == null || audioSource == null)
        {
            Debug.LogError("RhythmGame: Chart, Chart의 Audio, Audio Source가 모두 연결돼 있어야 합니다.");
            return;
        }

        beats = chart.BuildBeats();
        if (beats.Count == 0)
        {
            Debug.LogError("RhythmGame: 박이 없습니다. BeatTapper로 박 지도를 기록하거나 BPM을 설정하세요.");
            return;
        }
        judged = new bool[beats.Count];
        nextMissCheck = 0;

        // 미션 준비
        events.Clear();
        foreach (var ev in chart.events)
        {
            int idx = SongChart.FindBeat(beats, ev.measure, ev.beat);
            if (idx < 0)
            {
                Debug.LogWarning($"미션 {ev.type}: {ev.measure}마디 {ev.beat}박이 박 지도에 없어서 건너뜁니다.");
                continue;
            }
            var s = new EventState { ev = ev, beatIndex = idx };
            double st = beats[idx].time;
            if (ev.type == CueType.Cue || ev.type == CueType.Cutoff)
            {
                double bl = BeatLenAt(idx);
                s.judgeStart = st - 0.5 * bl;
                s.judgeEnd = st + 1.0 * bl;
            }
            else
            {
                int endIdx = Mathf.Min(idx + ev.lengthBeats, beats.Count - 1);
                s.judgeStart = st;
                s.judgeEnd = endIdx > idx ? beats[endIdx].time : st + ev.lengthBeats * BeatLenAt(idx);
            }
            events.Add(s);
        }
        events.Sort((a, b) => a.judgeStart.CompareTo(b.judgeStart));

        score = combo = maxCombo = perfect = great = good = miss = eventSuccess = eventFail = 0;
        feedback = eventFeedback = "";

        // 재생 범위: 첫 박 조금 앞 ~ 마지막 박 조금 뒤
        startOffset = System.Math.Max(0.0, beats[0].time - chart.leadIn);
        endTime = System.Math.Min(chart.audio.length, beats[beats.Count - 1].time + chart.endPadding);

        audioSource.Stop();
        audioSource.clip = chart.audio;
        audioSource.pitch = 1f;
        baseVolume = audioSource.volume > 0f ? audioSource.volume : 1f;
        audioSource.volume = baseVolume;
        audioSource.time = (float)startOffset;
        startDsp = AudioSettings.dspTime + startDelay;
        audioSource.PlayScheduled(startDsp);
        playing = true;
        finished = false;
    }

    // ───────────── 매 프레임 ─────────────

    private void Update()
    {
        if (!playing) return;

        double t = SongTime;
        double inputTime = t - inputLatency;

        while (nextMissCheck < beats.Count && beats[nextMissCheck].time + goodWindow < inputTime)
        {
            if (!judged[nextMissCheck])
            {
                judged[nextMissCheck] = true;
                miss++;
                combo = 0;
                ShowFeedback("Miss", new Color(0.6f, 0.6f, 0.6f));
            }
            nextMissCheck++;
        }

        UpdateEvents(inputTime);

        // 음원 중간에서 끝나면 마지막 1.5초 동안 서서히 소리 줄이기
        if (endTime < chart.audio.length - 0.1)
        {
            float fade = Mathf.Clamp01((float)((endTime - t) / 1.5));
            audioSource.volume = baseVolume * fade;
        }

        if (t > endTime)
        {
            audioSource.Stop();
            audioSource.volume = baseVolume;
            playing = false;
            finished = true;
        }
    }

    // ───────────── 박 판정 ─────────────

    private void HandleBeat(int beatInMeasure)
    {
        if (!playing) return;

        double t = SongTime - inputLatency;
        int idx = NearestBeat(t);
        if (idx < 0 || judged[idx]) return;

        double diff = t - beats[idx].time;           // -면 빠름, +면 느림
        double err = System.Math.Abs(diff);
        if (err > goodWindow) return;
        judged[idx] = true;

        int baseScore;
        string label;
        Color color;
        if (err <= perfectWindow) { baseScore = perfectScore; label = "Perfect"; color = new Color(1f, 0.85f, 0.2f); perfect++; }
        else if (err <= greatWindow) { baseScore = greatScore; label = "Great"; color = new Color(0.4f, 0.9f, 0.4f); great++; }
        else { baseScore = goodScore; label = "Good"; color = new Color(0.4f, 0.7f, 1f); good++; }

        if (err > perfectWindow) label += diff < 0 ? " (빠름)" : " (느림)";

        combo++;
        maxCombo = Mathf.Max(maxCombo, combo);
        int gained = Mathf.RoundToInt(baseScore * (1f + Mathf.Min(combo, 50) * 0.02f));

        var b = beats[idx];
        if (b.measure > 0 && b.beatNum == 1 && beatInMeasure == 1)
        {
            gained += downbeatBonus;
            label += "  1박!";
        }

        score += gained;
        ShowFeedback(label, color);
    }

    /// <summary>시각 t에 가장 가까운 박 번호</summary>
    private int NearestBeat(double t)
    {
        int i = FloorBeat(t);
        if (i < 0) return beats.Count > 0 ? 0 : -1;
        if (i + 1 < beats.Count && beats[i + 1].time - t < t - beats[i].time) return i + 1;
        return i;
    }

    /// <summary>시각 t 이전(같음 포함)의 마지막 박 번호. 첫 박 전이면 -1. (이진 탐색)</summary>
    private int FloorBeat(double t)
    {
        int lo = 0, hi = beats.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (beats[mid].time <= t) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>i번째 박의 길이(다음 박까지 시간)</summary>
    private double BeatLenAt(int i)
    {
        if (i + 1 < beats.Count) return beats[i + 1].time - beats[i].time;
        if (i > 0) return beats[i].time - beats[i - 1].time;
        return 0.5;
    }

    // ───────────── 왼손 미션 판정 ─────────────

    private void UpdateEvents(double t)
    {
        foreach (var s in events)
        {
            if (s.done || t < s.judgeStart) continue;

            if (!s.active)
            {
                s.active = true;
                s.startHeight = -1f;
                s.held = 0f;
            }

            if (leftHand != null && leftHand.HasHand)
            {
                float h = leftHand.Height;
                if (s.startHeight < 0f) { s.startHeight = h; s.minH = s.maxH = h; }
                s.minH = Mathf.Min(s.minH, h);
                s.maxH = Mathf.Max(s.maxH, h);

                if (s.ev.type == CueType.Cue && leftHand.IsRaised && leftHand.CurrentZone == s.ev.section)
                    s.held += Time.deltaTime;
                if (s.ev.type == CueType.Cutoff && leftHand.IsClosed)
                    s.held += Time.deltaTime;
            }

            bool success = s.ev.type switch
            {
                CueType.Crescendo => s.startHeight >= 0f && s.maxH - s.startHeight >= dynamicsChange,
                CueType.Decrescendo => s.startHeight >= 0f && s.startHeight - s.minH >= dynamicsChange,
                _ => s.held >= holdTime,
            };

            if (success) CompleteEvent(s, true);
            else if (t > s.judgeEnd) CompleteEvent(s, false);
        }
    }

    private void CompleteEvent(EventState s, bool success)
    {
        s.done = true;
        s.success = success;
        if (success)
        {
            eventSuccess++;
            score += eventScore;
            eventFeedback = $"{EventName(s.ev)} 성공!  +{eventScore}";
            eventFeedbackColor = new Color(1f, 0.85f, 0.2f);
        }
        else
        {
            eventFail++;
            eventFeedback = $"{EventName(s.ev)} 놓침";
            eventFeedbackColor = new Color(0.7f, 0.7f, 0.7f);
        }
        eventFeedbackTime = Time.time;
    }

    private static string EventName(ChartEvent ev) => ev.type switch
    {
        CueType.Crescendo => "크레센도",
        CueType.Decrescendo => "디크레센도",
        CueType.Cue => $"{SongChart.SectionName(ev.section)} 큐",
        _ => "컷오프",
    };

    private static string EventInstruction(ChartEvent ev) => ev.type switch
    {
        CueType.Crescendo => "왼손을 천천히 위로 올리세요",
        CueType.Decrescendo => "왼손을 천천히 아래로 내리세요",
        CueType.Cue => $"왼손을 들어 {SongChart.SectionName(ev.section)} 쪽을 가리키세요",
        _ => "왼손을 꽉 쥐세요 (또는 엄지와 검지를 붙이기)",
    };

    private void ShowFeedback(string text, Color color)
    {
        feedback = text;
        feedbackColor = color;
        feedbackTime = Time.time;
    }

    // ───────────── 화면 ─────────────

    private GUIStyle mid, big, huge, box;

    private void OnGUI()
    {
        mid ??= new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
        big ??= new GUIStyle(GUI.skin.label) { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        huge ??= new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        box ??= new GUIStyle(GUI.skin.button) { fontSize = 28, fontStyle = FontStyle.Bold };

        float cx = Screen.width / 2f;

        if (!playing && !finished)
        {
            string title = chart != null ? chart.title : "(곡 없음)";
            if (GUI.Button(new Rect(cx - 250, Screen.height / 2f - 40, 500, 80), $"시작: {title}", box))
                StartSong();
            return;
        }

        if (finished)
        {
            int totalJudged = perfect + great + good + miss;
            float acc = totalJudged > 0 ? (perfect * 1f + great * 0.7f + good * 0.4f) / totalJudged * 100f : 0f;
            GUI.Box(new Rect(cx - 260, 80, 520, 420), "");
            GUI.Label(new Rect(cx - 260, 100, 520, 60), "연주 완료!", huge);
            GUI.Label(new Rect(cx - 260, 170, 520, 50), $"점수 {score:N0}", big);
            GUI.Label(new Rect(cx - 220, 230, 480, 34), $"Perfect {perfect}  Great {great}  Good {good}  Miss {miss}", mid);
            GUI.Label(new Rect(cx - 220, 266, 480, 34), $"정확도 {acc:0.0}%   최대 콤보 {maxCombo}", mid);
            GUI.Label(new Rect(cx - 220, 302, 480, 34), $"왼손 미션 {eventSuccess} / {eventSuccess + eventFail}", mid);
            if (GUI.Button(new Rect(cx - 150, 400, 300, 70), "다시 하기", box)) StartSong();
            return;
        }

        float rx = Screen.width - 400;
        double t = SongTime;
        GUI.color = Color.white;
        GUI.Label(new Rect(rx, 20, 380, 34), $"점수  {score:N0}", mid);
        GUI.Label(new Rect(rx, 54, 380, 34), combo > 1 ? $"콤보  {combo}" : "", mid);

        // 음악의 현재 마디/박 + 메트로놈 깜빡임
        int cur = FloorBeat(t);
        if (cur < 0)
        {
            int beatsLeft = Mathf.CeilToInt((float)((beats[0].time - t) / BeatLenAt(0)));
            GUI.Label(new Rect(rx, 96, 380, 34), $"시작까지 {beatsLeft}박", mid);
        }
        else
        {
            var b = beats[cur];
            double bl = BeatLenAt(cur);
            float phase = (float)((t - b.time) / bl);
            string where = b.measure > 0 ? $"{b.measure}마디 {b.beatNum}박 ({b.measureLength}박 마디)" : "못갖춘마디";
            GUI.Label(new Rect(rx, 96, 380, 34), where, mid);
            GUI.Label(new Rect(rx, 130, 380, 34), $"템포 약 {60.0 / bl:0}", mid);

            bool down = b.beatNum == 1 && b.measure > 0;
            GUI.color = new Color(1f, down ? 0.3f : 1f, down ? 0.3f : 1f, Mathf.Clamp01(1f - phase * 3f));
            GUI.DrawTexture(new Rect(rx, 170, 40, 40), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        float fa = 1f - (Time.time - feedbackTime) / 0.6f;
        if (fa > 0f)
        {
            GUI.color = new Color(feedbackColor.r, feedbackColor.g, feedbackColor.b, fa);
            GUI.Label(new Rect(cx - 300, 30, 600, 60), feedback, huge);
            GUI.color = Color.white;
        }

        DrawEventPrompt(cx, t - inputLatency);

        float ea = 1f - (Time.time - eventFeedbackTime) / 1.2f;
        if (ea > 0f)
        {
            GUI.color = new Color(eventFeedbackColor.r, eventFeedbackColor.g, eventFeedbackColor.b, ea);
            GUI.Label(new Rect(cx - 400, Screen.height - 180, 800, 50), eventFeedback, big);
            GUI.color = Color.white;
        }
    }

    private void DrawEventPrompt(float cx, double t)
    {
        int cur = Mathf.Max(FloorBeat(t), 0);
        foreach (var s in events)
        {
            if (s.done) continue;

            if (s.active)
            {
                GUI.color = new Color(1f, 0.9f, 0.3f);
                GUI.Label(new Rect(cx - 450, Screen.height - 130, 900, 50), $"지금! {EventInstruction(s.ev)}", big);

                if (s.ev.type == CueType.Crescendo || s.ev.type == CueType.Decrescendo)
                {
                    float moved = s.startHeight < 0f ? 0f
                        : s.ev.type == CueType.Crescendo ? s.maxH - s.startHeight : s.startHeight - s.minH;
                    float p = Mathf.Clamp01(moved / dynamicsChange);
                    GUI.color = new Color(1f, 1f, 1f, 0.3f);
                    GUI.DrawTexture(new Rect(cx - 200, Screen.height - 70, 400, 16), Texture2D.whiteTexture);
                    GUI.color = new Color(1f, 0.9f, 0.3f);
                    GUI.DrawTexture(new Rect(cx - 200, Screen.height - 70, 400 * p, 16), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
                return;
            }

            int beatsUntil = s.beatIndex - cur;
            if (beatsUntil > 0 && beatsUntil <= 8)              // 8박 전부터 예고
            {
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                GUI.Label(new Rect(cx - 450, Screen.height - 130, 900, 50), $"곧: {EventName(s.ev)}  ({beatsUntil}박 후)", big);
                GUI.color = Color.white;
                return;
            }
        }
    }
}