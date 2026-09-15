// QuitOnKeyPress.cs
//
// 빌드된 실행 파일(.exe)에는 Unity 에디터의 정지 버튼이 없어서, 시연 중 끄기 편하도록
// 특정 키를 누르면 프로그램을 종료하는 기능만 담당하는 스크립트.
//
// 사용법: 씬 안 아무 오브젝트에나 붙이면 됩니다 (새 빈 오브젝트를 만들어서 붙여도 되고,
// 이미 있는 매니저 성격의 오브젝트에 같이 붙여도 무방).

using UnityEngine;

public class QuitOnKeyPress : MonoBehaviour
{
    [Tooltip("이 키를 누르면 프로그램을 종료합니다. 기본값은 Esc.")]
    public KeyCode quitKey = KeyCode.Escape;

    void Update()
    {
        if (Input.GetKeyDown(quitKey))
        {
            Debug.Log($"[QuitOnKeyPress] {quitKey} 입력 -> 프로그램 종료");

#if UNITY_EDITOR
            // 에디터에서 Play 중이면 Play 모드만 정지 (실제 에디터 자체를 끄지 않음)
            UnityEditor.EditorApplication.isPlaying = false;
#else
            // 빌드된 실행 파일에서는 실제로 프로그램 종료
            Application.Quit();
#endif
        }
    }
}
