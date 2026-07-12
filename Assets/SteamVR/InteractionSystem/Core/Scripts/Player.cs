//======= Custom replacement (SteamVR/OpenVR 의존성 제거 버전) ===============
//
// 원본 Player.cs는 SteamVR 헤드셋/컨트롤러 리그 전체(HMD, 양손, 트래커 목록,
// 룸스케일 경계 등)를 관리하는 큰 클래스입니다. 여기서는 Throwable.cs가 실제로
// 사용하는 최소 기능(Player.instance, trackingOriginTransform)만 남겼습니다.
//
//=============================================================================

using UnityEngine;

namespace Valve.VR.InteractionSystem
{
    public class Player : MonoBehaviour
    {
        public static Player instance;

        [Tooltip("플레이 공간의 기준 원점. 스케일 계산(SteamVR_Utils.GetLossyScale)에 쓰입니다. 보통 이 오브젝트 자신이면 충분합니다.")]
        public Transform trackingOriginTransform;

        [Tooltip("왼손 Hand 컴포넌트")]
        public Hand leftHand;
        [Tooltip("오른손 Hand 컴포넌트")]
        public Hand rightHand;

        protected virtual void Awake()
        {
            instance = this;

            if (trackingOriginTransform == null)
                trackingOriginTransform = this.transform;

            // 양손이 인스펙터에 할당돼 있으면 서로를 otherHand로 자동 연결
            if (leftHand != null && rightHand != null)
            {
                leftHand.otherHand = rightHand;
                rightHand.otherHand = leftHand;
            }
        }

        protected virtual void OnEnable()
        {
            instance = this;
        }

        // DebugUI.cs가 OnGUI에서 호출합니다. 화면 왼쪽 위에 손 상태를 텍스트로 표시.
        // 실제 IMU/ArUco/포텐셔미터 값이 들어오기 시작하면 여기에 값 확인하기 좋습니다.
        public void Draw2DDebug()
        {
            float y = 10f;
            const float lineHeight = 22f;

            DrawHandDebug(ref y, lineHeight, "Left", leftHand);
            DrawHandDebug(ref y, lineHeight, "Right", rightHand);
        }

        private void DrawHandDebug(ref float y, float lineHeight, string label, Hand hand)
        {
            if (hand == null) return;

            GUI.Label(new Rect(10, y, 500, lineHeight), $"[{label}] pos={hand.transform.position:F2} curl={hand.grabCurl:F2} grabbing={(hand.grabCurl >= hand.grabThreshold)}");
            y += lineHeight;

            string hoverName = hand.hoveringInteractable != null ? hand.hoveringInteractable.name : "-";
            string attachedName = hand.currentAttachedObject != null ? hand.currentAttachedObject.name : "-";
            GUI.Label(new Rect(10, y, 500, lineHeight), $"[{label}] hovering={hoverName} attached={attachedName} active={hand.isActive}");
            y += lineHeight;
        }
    }
}
