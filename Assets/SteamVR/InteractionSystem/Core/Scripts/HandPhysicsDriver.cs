//======= Custom replacement for HandPhysics.cs =======
//
// 원본 HandPhysics.cs는 OpenVR 스켈레톤 입력(손가락 하나하나의 실제 관절 데이터)을 받아서
// 손가락별 콜라이더 위치까지 물리적으로 맞춰주는 정교한 시스템이었습니다.
// 우리는 그 정도로 세밀한 손가락 관절 데이터가 없으므로(포텐셔미터 curl 값은
// 손가락 하나당 굽힘 정도 하나뿐), 손 전체를 하나의 물리 바디로 취급하는
// 단순화된 버전으로 대체합니다.
//
// 역할: Hand의 transform(ArUco 위치 + IMU 회전으로 매 프레임 갱신됨)을 목표로,
// HandCollider(Rigidbody 기반)가 물리적으로 따라가게 만들어서 벽/책상 같은
// 정적 지오메트리를 뚫고 지나가지 않도록 합니다.
//
//=====================================================================

using UnityEngine;

namespace Valve.VR.InteractionSystem
{
    [RequireComponent(typeof(Hand))]
    public class HandPhysicsDriver : MonoBehaviour
    {
        [Tooltip("물리 충돌을 담당할 HandCollider 프리팹")]
        public HandCollider handColliderPrefab;
        [HideInInspector]
        public HandCollider handCollider;

        [Tooltip("이 반경 안에 뭔가 있으면(물체를 잡고 있다가 놓았을 때 등) 충돌을 다시 켜기 전 대기")]
        public float collisionReenableClearanceRadius = 0.1f;

        private Hand hand;
        private bool collisionsEnabled = true;
        private Collider[] clearanceBuffer = new Collider[1];

        // 목표 위치와 실제 물리 위치가 이 거리보다 벌어지면 순간이동으로 따라잡음
        public float handResetDistance = 0.6f;

        private void Awake()
        {
            hand = GetComponent<Hand>();
        }

        private void Start()
        {
            if (handColliderPrefab == null)
            {
                Debug.LogWarning("[HandPhysicsDriver] handColliderPrefab이 지정되지 않아 물리 충돌을 건너뜁니다.");
                enabled = false;
                return;
            }

            handCollider = Instantiate(handColliderPrefab.gameObject).GetComponent<HandCollider>();
            handCollider.transform.SetParent(Player.instance != null ? Player.instance.transform : null);
            handCollider.hand = hand;
            handCollider.TeleportTo(hand.transform.position, hand.transform.rotation);
        }

        private void FixedUpdate()
        {
            if (handCollider == null) return;

            // 뭔가를 잡고 있는 동안은 손 자체의 충돌을 꺼서 잡은 물체와 다투지 않게 함
            if (hand.currentAttachedObject != null)
            {
                collisionsEnabled = false;
            }
            else if (!collisionsEnabled)
            {
                clearanceBuffer[0] = null;
                Physics.OverlapSphereNonAlloc(hand.objectAttachmentPoint.position, collisionReenableClearanceRadius, clearanceBuffer);
                if (clearanceBuffer[0] == null)
                    collisionsEnabled = true;
            }

            handCollider.SetCollisionDetectionEnabled(collisionsEnabled);
            handCollider.MoveTo(hand.transform.position, hand.transform.rotation);

            float sqDist = (handCollider.transform.position - hand.transform.position).sqrMagnitude;
            if (sqDist > handResetDistance * handResetDistance)
            {
                handCollider.TeleportTo(hand.transform.position, hand.transform.rotation);
            }
        }
    }
}
