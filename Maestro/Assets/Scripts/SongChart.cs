using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>왼손 표현 미션 종류</summary>
public enum CueType
{
    Crescendo,    // 점점 세게: 왼손을 천천히 올리기
    Decrescendo,  // 점점 여리게: 왼손을 천천히 내리기
    Cue,          // 파트 큐: 해당 파트 쪽으로 왼손을 들어 가리키기
    Cutoff,       // 컷오프: 왼손을 꽉 쥐거나 엄지·검지 붙이기
}

/// <summary>곡 중간에 나오는 미션 하나</summary>
[Serializable]
public class ChartEvent
{
    public CueType type = CueType.Crescendo;

    [Min(1)] public int measure = 1;        // 몇 번째 마디에서 (1부터)
    [Min(1)] public int beat = 1;           // 그 마디의 몇 번째 박에서 (1부터)

    [Tooltip("크레센도/디크레센도가 이어지는 길이(박). 큐/컷오프는 무시됨")]
    [Min(1)] public int lengthBeats = 4;

    [Tooltip("큐일 때 가리킬 파트 (왼쪽 = 바이올린, 가운데 = 목관, 오른쪽 = 첼로)")]
    public LeftHandGestures.Zone section = LeftHandGestures.Zone.Left;
}

/// <summary>탭으로 기록한 박 하나</summary>
[Serializable]
public struct BeatMark
{
    public float time;       // 음원에서의 시각(초)
    public bool downbeat;    // 마디의 1박인지

    public BeatMark(float time, bool downbeat)
    {
        this.time = time;
        this.downbeat = downbeat;
    }
}

/// <summary>
/// 곡 하나의 정보. Project 창에서 우클릭 → Create → Maestro → Song Chart 로 만든다.
/// 박 지도(Beat Map)가 있으면 그걸 쓰고, 없으면 고정 템포(BPM)로 박을 만든다.
/// </summary>
[CreateAssetMenu(fileName = "NewSongChart", menuName = "Maestro/Song Chart")]
public class SongChart : ScriptableObject
{
    /// <summary>게임에서 쓰는 박 하나의 정보</summary>
    public struct BeatInfo
    {
        public double time;        // 음원에서의 시각(초)
        public int measure;        // 몇 마디 (첫 1박 이전의 못갖춘마디는 0)
        public int beatNum;        // 마디 안에서 몇 박째 (1부터)
        public int measureLength;  // 이 마디가 몇 박짜리인지
    }

    [Header("곡 정보")]
    public string title = "새 곡";
    public AudioClip audio;

    [Header("박 지도 (BeatTapper로 기록. 있으면 이걸 우선 사용)")]
    public List<BeatMark> beatMap = new List<BeatMark>();

    [Header("박 지도가 없을 때만: 고정 템포")]
    [Min(20f)] public float bpm = 120f;
    [Range(1, 6)] public int beatsPerMeasure = 4;
    [Tooltip("음원에서 1마디 1박이 시작되는 시각(초)")]
    [Min(0f)] public float firstBeatTime = 0f;
    [Tooltip("판정할 마디 수. 0이면 음원 끝까지")]
    [Min(0)] public int totalMeasures = 0;

    [Header("재생 범위")]
    [Tooltip("첫 박보다 몇 초 앞에서 재생을 시작할지")]
    [Min(0f)] public float leadIn = 3f;
    [Tooltip("마지막 박 뒤로 몇 초 더 재생하고 끝낼지 (음원 중간에서 끝날 때 서서히 줄어듦)")]
    [Min(0f)] public float endPadding = 3f;

    [Header("왼손 미션")]
    public List<ChartEvent> events = new List<ChartEvent>();

    /// <summary>게임에서 쓸 박 목록을 만든다.</summary>
    public List<BeatInfo> BuildBeats()
    {
        var beats = new List<BeatInfo>();

        if (beatMap != null && beatMap.Count > 0)
        {
            var sorted = new List<BeatMark>(beatMap);
            sorted.Sort((a, b) => a.time.CompareTo(b.time));

            int measure = 0, beat = 0;
            foreach (var m in sorted)
            {
                if (m.downbeat) { measure++; beat = 1; }
                else beat++;
                beats.Add(new BeatInfo { time = m.time, measure = measure, beatNum = beat });
            }
        }
        else if (audio != null)
        {
            double len = 60.0 / bpm;
            int total = totalMeasures > 0
                ? totalMeasures * beatsPerMeasure
                : Mathf.Max(0, Mathf.FloorToInt((float)((audio.length - firstBeatTime) / len)) + 1);
            for (int i = 0; i < total; i++)
            {
                beats.Add(new BeatInfo
                {
                    time = firstBeatTime + i * len,
                    measure = i / beatsPerMeasure + 1,
                    beatNum = i % beatsPerMeasure + 1,
                });
            }
        }

        // 각 마디가 몇 박짜리인지 채우기
        int start = 0;
        for (int i = 1; i <= beats.Count; i++)
        {
            if (i == beats.Count || beats[i].measure != beats[start].measure)
            {
                int length = i - start;
                for (int j = start; j < i; j++)
                {
                    var b = beats[j];
                    b.measureLength = length;
                    beats[j] = b;
                }
                start = i;
            }
        }

        return beats;
    }

    /// <summary>(마디, 박)에 해당하는 박 번호. 없으면 -1.</summary>
    public static int FindBeat(List<BeatInfo> beats, int measure, int beat)
    {
        for (int i = 0; i < beats.Count; i++)
            if (beats[i].measure == measure && beats[i].beatNum == beat) return i;
        return -1;
    }

    public static string SectionName(LeftHandGestures.Zone z) => z switch
    {
        LeftHandGestures.Zone.Left => "바이올린 (왼쪽)",
        LeftHandGestures.Zone.Right => "첼로 (오른쪽)",
        _ => "목관 (가운데)",
    };
}