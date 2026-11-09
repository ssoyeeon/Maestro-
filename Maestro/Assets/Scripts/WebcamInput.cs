using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 1단계: 웹캠 영상을 받아 화면(RawImage)에 표시한다.
/// 이후 단계에서 CamTexture를 MediaPipe 등 인식 모듈에 넘겨 사용한다.
/// </summary>
public class WebcamInput : MonoBehaviour
{
    [Header("표시 대상")]
    [SerializeField] private RawImage display;
    [SerializeField] private AspectRatioFitter fitter;

    [Header("카메라 설정")]
    [SerializeField] private int deviceIndex = 0;        // 콘솔에 출력되는 목록 번호
    [SerializeField] private int requestedWidth = 1280;  // 요청값일 뿐, 실제 해상도는 기기에 따라 다를 수 있음
    [SerializeField] private int requestedHeight = 720;
    [SerializeField] private int requestedFPS = 30;
    [SerializeField] private bool mirror = true;         // 거울처럼 보이게 좌우 반전 (지휘할 때 자연스러움)

    public WebCamTexture CamTexture { get; private set; }

    // 웹캠은 시작 직후 몇 프레임 동안 16x16 같은 임시 크기를 반환하므로 그걸 걸러낸다.
    public bool IsReady => CamTexture != null && CamTexture.isPlaying && CamTexture.width > 16;

    private IEnumerator Start()
    {
        // 권한 요청 (Windows/Mac 에디터에선 보통 바로 통과, 모바일·웹에선 필수)
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("웹캠 권한이 없습니다.");
            yield break;
        }

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("연결된 웹캠을 찾을 수 없습니다.");
            yield break;
        }

        for (int i = 0; i < devices.Length; i++)
            Debug.Log($"[웹캠 {i}] {devices[i].name}");

        deviceIndex = Mathf.Clamp(deviceIndex, 0, devices.Length - 1);
        CamTexture = new WebCamTexture(devices[deviceIndex].name, requestedWidth, requestedHeight, requestedFPS);
        display.texture = CamTexture;
        CamTexture.Play();

        // 실제 해상도가 정해질 때까지 대기
        while (!IsReady)
            yield return null;

        Debug.Log($"웹캠 시작: {devices[deviceIndex].name} / 실제 해상도 {CamTexture.width}x{CamTexture.height}");
    }

    private void Update()
    {
        if (!IsReady) return;

        // 영상 비율에 맞춰 찌그러짐 방지
        if (fitter != null)
            fitter.aspectRatio = (float)CamTexture.width / CamTexture.height;

        // 일부 기기는 영상이 회전·상하반전되어 들어오므로 보정
        display.rectTransform.localEulerAngles = new Vector3(0f, 0f, -CamTexture.videoRotationAngle);

        float scaleX = mirror ? -1f : 1f;
        float scaleY = CamTexture.videoVerticallyMirrored ? -1f : 1f;
        display.rectTransform.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    private void OnDisable()
    {
        if (CamTexture != null && CamTexture.isPlaying)
            CamTexture.Stop();
    }
}