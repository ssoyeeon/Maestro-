using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 박 지도 기록 도구. 음악을 들으며 1박은 F, 나머지 박은 J를 눌러 SongChart에 박 시각을 저장한다.
/// 사용법: 빈 씬에 GameObject를 만들고 이 스크립트와 AudioSource를 붙인 뒤 Play.
///         Game 화면을 한 번 클릭해야 키보드 입력이 들어간다.
/// </summary>
public class BeatTapper : MonoBehaviour
{
    [SerializeField] private SongChart chart;
    [SerializeField] private AudioSource audioSource;

    [Header("키")]
    [SerializeField] private KeyCode downbeatKey = KeyCode.F;   // 1박
    [SerializeField] private KeyCode beatKey = KeyCode.J;       // 나머지 박
    [SerializeField] private KeyCode undoKey = KeyCode.Backspace;
    [SerializeField] private KeyCode playKey = KeyCode.Space;

    [Header("보정")]
    [Tooltip("탭이 박보다 늘 늦게 기록되면 올리고(예: 0.03), 빠르면 내린다")]
    [SerializeField] private float tapOffset = 0f;

    private static readonly float[] Speeds = { 1f, 0.75f, 0.5f };
    private int speedIdx;

    private bool ready, playing, recording, clickCheck = true;
    private double anchorClip, anchorReal;   // 재생 위치 계산용 시계
    private int nextClickIdx;
    private readonly Stack<float> undoStack = new Stack<float>();
    private readonly HashSet<KeyCode> held = new HashSet<KeyCode>();

    private AudioSource clickSource;
    private AudioClip clickHigh, clickLow;
    private float flashTime = -99f;
    private bool flashDownbeat;
    private float clearArmedTime = -99f;
    private string status = "";

    private List<BeatMark> Map => chart.beatMap;
    private float Pitch => Speeds[speedIdx];
    private double Now => Time.realtimeSinceStartupAsDouble;

    private double ClipTime => playing ? anchorClip + (Now - anchorReal) * Pitch : anchorClip;

    private void Start()
    {
        clickSource = gameObject.AddComponent<AudioSource>();
        clickSource.playOnAwake = false;
        clickHigh = MakeClick(1600f);
        clickLow = MakeClick(1000f);

        if (chart == null || chart.audio == null || audioSource == null)
        {
            status = "Chart, Chart의 Audio, Audio Source를 연결하세요";
            return;
        }
        audioSource.playOnAwake = false;
        audioSource.clip = chart.audio;
        if (chart.beatMap == null) chart.beatMap = new List<BeatMark>();
        SortMap();
        ready = true;
    }

    private void Update()
    {
        if (!ready || !playing) return;

        // 오디오 재생 위치와 시계가 어긋나면 다시 맞춤
        if (audioSource.isPlaying && System.Math.Abs(audioSource.time - ClipTime) > 0.08)
        {
            anchorClip = audioSource.time;
            anchorReal = Now;
        }

        if (!audioSource.isPlaying && ClipTime >= chart.audio.length - 0.05)
        {
            Pause();
            anchorClip = chart.audio.length;
            return;
        }

        // 확인 모드: 기록된 박마다 클릭 소리
        double t = ClipTime;
        while (nextClickIdx < Map.Count && Map[nextClickIdx].time <= t)
        {
            if (clickCheck && !recording && t - Map[nextClickIdx].time < 0.1)
                clickSource.PlayOneShot(Map[nextClickIdx].downbeat ? clickHigh : clickLow, 0.8f);
            nextClickIdx++;
        }
    }

    // ───── 재생 제어 ─────

    private void Play()
    {
        audioSource.pitch = Pitch;
        audioSource.time = Mathf.Clamp((float)anchorClip, 0f, chart.audio.length - 0.01f);
        audioSource.Play();
        anchorReal = Now;
        playing = true;
        ResetClickIndex();
    }

    private void Pause()
    {
        anchorClip = ClipTime;
        audioSource.Pause();
        playing = false;
        if (recording) StopRecording();
    }

    private void Seek(double t)
    {
        anchorClip = System.Math.Clamp(t, 0.0, chart.audio.length - 0.01);
        anchorReal = Now;
        if (playing) audioSource.time = (float)anchorClip;
        ResetClickIndex();
    }

    private void ResetClickIndex()
    {
        double t = ClipTime;
        nextClickIdx = 0;
        while (nextClickIdx < Map.Count && Map[nextClickIdx].time < t) nextClickIdx++;
    }

    // ───── 기록 ─────

    private void StartRecording()
    {
        // 현재 위치 이후 기록은 지우고 새로 기록 (덮어쓰기)
        float from = (float)ClipTime - 0.05f;
        int removed = Map.RemoveAll(m => m.time >= from);
        undoStack.Clear();
        recording = true;
        status = removed > 0 ? $"{Fmt(from)} 이후 기존 박 {removed}개를 지우고 기록 시작" : "기록 시작";
        if (!playing) Play();
    }

    private void StopRecording()
    {
        recording = false;
        SortMap();
        Save();
        status = $"기록 저장됨 (박 {Map.Count}개)";
    }

    private void AddMark(bool downbeat)
    {
        float t = (float)ClipTime - tapOffset;
        Map.Add(new BeatMark(t, downbeat));
        undoStack.Push(t);
        flashTime = Time.time;
        flashDownbeat = downbeat;
    }

    private void Undo()
    {
        if (undoStack.Count == 0) return;
        float t = undoStack.Pop();
        int idx = Map.FindLastIndex(m => Mathf.Approximately(m.time, t));
        if (idx >= 0) Map.RemoveAt(idx);
        status = "마지막 탭 취소";
    }

    private void SortMap()
    {
        Map.Sort((a, b) => a.time.CompareTo(b.time));
    }

    private void Save()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(chart);
        UnityEditor.AssetDatabase.SaveAssets();
#endif
    }

    // ───── 입력 & 화면 ─────

    private GUIStyle label, big, button;

    private void OnGUI()
    {
        label ??= new GUIStyle(GUI.skin.label) { fontSize = 20 };
        big ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
        button ??= new GUIStyle(GUI.skin.button) { fontSize = 18 };

        if (!ready) { GUI.Label(new Rect(20, 20, 900, 40), status, big); return; }
        HandleKeys();

        float x = 20, y = 20, w = Mathf.Min(Screen.width - 40, 900);
        double t = ClipTime;
        float len = chart.audio.length;

        GUI.Label(new Rect(x, y, w, 40), chart.title, big); y += 44;
        GUI.Label(new Rect(x, y, w, 30), $"{Fmt(t)} / {Fmt(len)}   속도 x{Pitch}   {(recording ? "● 기록 중" : "")}", label); y += 34;

        // 탐색 막대
        float newT = GUI.HorizontalSlider(new Rect(x, y, w, 24), (float)t, 0f, len);
        if (Mathf.Abs(newT - (float)t) > 0.25f && !recording) Seek(newT);
        y += 34;

        // 버튼들
        float bx = x;
        if (GUI.Button(new Rect(bx, y, 140, 40), playing ? "일시정지" : "재생", button)) { if (playing) Pause(); else Play(); }
        bx += 150;
        if (GUI.Button(new Rect(bx, y, 90, 40), "-5초", button) && !recording) Seek(t - 5); bx += 100;
        if (GUI.Button(new Rect(bx, y, 90, 40), "+5초", button) && !recording) Seek(t + 5); bx += 100;
        if (GUI.Button(new Rect(bx, y, 120, 40), $"속도 x{Pitch}", button) && !recording)
        {
            speedIdx = (speedIdx + 1) % Speeds.Length;
            if (playing) { Pause(); Play(); }
        }
        bx += 130;
        if (GUI.Button(new Rect(bx, y, 160, 40), clickCheck ? "확인 클릭: 켬" : "확인 클릭: 끔", button)) clickCheck = !clickCheck;
        y += 50;

        bx = x;
        GUI.color = recording ? new Color(1f, 0.5f, 0.5f) : Color.white;
        if (GUI.Button(new Rect(bx, y, 260, 44), recording ? "기록 멈추고 저장" : "여기부터 기록 (덮어쓰기)", button))
        {
            if (recording) StopRecording(); else StartRecording();
        }
        GUI.color = Color.white;
        bx += 270;
        if (GUI.Button(new Rect(bx, y, 140, 44), "마지막 취소", button)) Undo(); bx += 150;

        bool armed = Time.time - clearArmedTime < 2f;
        if (GUI.Button(new Rect(bx, y, 160, 44), armed ? "정말 전부 삭제?" : "전부 삭제", button))
        {
            if (armed) { Map.Clear(); undoStack.Clear(); Save(); status = "박 지도를 모두 지웠습니다"; clearArmedTime = -99f; }
            else clearArmedTime = Time.time;
        }
        y += 56;

        // 정보
        int downbeats = Map.FindAll(m => m.downbeat).Count;
        GUI.Label(new Rect(x, y, w, 30), $"기록된 박 {Map.Count}개 / 마디 {downbeats}개   현재 템포 약 {LocalBpm(t):0} BPM", label); y += 32;
        GUI.Label(new Rect(x, y, w, 30), status, label); y += 40;

        GUI.Label(new Rect(x, y, w, 30), $"[{downbeatKey}] 1박   [{beatKey}] 나머지 박   [{undoKey}] 마지막 취소   [{playKey}] 재생/일시정지", label);
        y += 40;

        // 탭할 때 번쩍임
        float fa = 1f - (Time.time - flashTime) / 0.25f;
        if (fa > 0f)
        {
            GUI.color = flashDownbeat ? new Color(1f, 0.3f, 0.3f, fa) : new Color(0.3f, 0.7f, 1f, fa);
            GUI.DrawTexture(new Rect(x, y, flashDownbeat ? 90 : 60, flashDownbeat ? 90 : 60), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }

    private void HandleKeys()
    {
        Event e = Event.current;
        if (e.type == EventType.KeyUp) { held.Remove(e.keyCode); return; }
        if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None) return;
        if (held.Contains(e.keyCode)) { e.Use(); return; }   // 누르고 있을 때 반복 입력 무시
        held.Add(e.keyCode);

        if (e.keyCode == playKey) { if (playing) Pause(); else Play(); }
        else if (e.keyCode == undoKey) Undo();
        else if (recording && playing && e.keyCode == downbeatKey) AddMark(true);
        else if (recording && playing && e.keyCode == beatKey) AddMark(false);
        e.Use();
    }

    /// <summary>현재 위치 근처의 박 간격으로 템포 추정</summary>
    private float LocalBpm(double t)
    {
        if (Map.Count < 2) return 0f;
        int i = Map.FindLastIndex(m => m.time <= t);
        if (i < 1) i = 1;
        if (i >= Map.Count) i = Map.Count - 1;
        float interval = Map[i].time - Map[i - 1].time;
        return interval > 0.0001f ? 60f / interval : 0f;
    }

    private static string Fmt(double s)
    {
        int m = (int)(s / 60);
        return $"{m}:{s - m * 60:00.0}";
    }

    private static AudioClip MakeClick(float freq)
    {
        int rate = 44100, n = rate / 25;    // 40ms
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = (float)i / rate;
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * tt) * Mathf.Exp(-tt * 80f);
        }
        var clip = AudioClip.Create("Click", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}