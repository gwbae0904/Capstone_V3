//======= Custom replacement for RenderModel.cs / ControllerHoverHighlight.cs =======
//
// 원본은 OpenVR 런타임에서 실제 컨트롤러의 3D 모델 파일을 그때그때 내려받아
// 렌더링하는 시스템이었습니다 (SteamVR_RenderModel). 우리는 OpenVR을 안 쓰니
// 이 방식 자체가 성립하지 않습니다.
//
// 대신 이 스크립트는 같은 "역할"만 가져옵니다:
//   1) 손 위치에 시각적으로 보이는 메시를 하나 붙여둔다
//   2) 물체를 hover 중이면 하이라이트 머티리얼로 바뀐다
//   3) 물체를 잡은 동안은 Interactable.hideHandOnAttach 설정에 따라 손을 숨긴다
//   4) 모델의 특정 뼈(보통 손목)가 항상 이 오브젝트의 원점에 오도록 매 프레임 재정렬한다
//      (마커 위치 = 렌더링된 손의 어느 부위에 대응될지를 정하는 부분)
//
// [수정 이력]
// - 4번 보정을 Awake()에서 딱 한 번만 계산하던 방식에서 매 프레임(LateUpdate)
//   재계산하는 방식으로 변경. 원인: Awake 시점에 RightHand의 회전이 나중에
//   IMU/ArUco가 실제로 적용하는 값과 다를 수 있어서, 한 번 계산한 오프셋이
//   그 이후 회전에 안 맞을 위험이 있었음(엄지 등 엉뚱한 지점이 회전축처럼
//   보이는 문제). 매 프레임 다시 맞추면 이런 타이밍 문제 자체가 없어짐.

using UnityEngine;

namespace Valve.VR.InteractionSystem
{
    [RequireComponent(typeof(Hand))]
    public class HandVisual : MonoBehaviour
    {
        [Tooltip("손을 나타낼 임시/실제 3D 모델 프리팹.")]
        public GameObject visualPrefab;

        [Tooltip("평상시 머티리얼 (비워두면 프리팹의 원래 머티리얼 유지)")]
        public Material normalMaterial;
        [Tooltip("hover 중일 때 적용할 머티리얼")]
        public Material highlightMaterial;

        [Tooltip("이 모델의 회전 중심(=마커 위치가 대응될 지점)을 특정 뼈에 맞출지 여부. 켜두면 아래 Target Bone Name 뼈를 찾아서 그 위치가 이 오브젝트의 원점에 항상 오도록 매 프레임 모델 전체를 재정렬합니다.")]
        public bool recenterOnWrist = true;
        [Tooltip("어느 뼈를 기준점으로 삼을지. 접미사(_r/_l)는 자동으로 붙습니다. 예: wrist(손목), root(손 전체 기준), finger_middle_meta(손등 근처), finger_thumb_0(엄지 시작점) 등")]
        public string targetBoneName = "wrist";
        [Tooltip("오른손 모델이면 _r, 왼손 모델이면 _l")]
        public string handSuffix = "_r";
        [Tooltip("뼈 기준점에서 추가로 더 밀고 싶은 만큼(로컬 기준, 미터 단위). 정확히 뼈와 뼈 사이 지점 등을 맞추고 싶을 때 미세 조정용")]
        public Vector3 additionalOffset = Vector3.zero;
        [Tooltip("모델 자체의 원래 조형(bind pose) 각도가 마음에 안 들 때(예: 악수하는 모양으로 보임), 여기서 추가로 돌려서 자연스러운 각도로 맞추세요. X/Y/Z 오일러 각도(도)")]
        public Vector3 visualRotationOffsetEuler = Vector3.zero;

        [Header("디버그")]
        public bool showDebugInfo = true;

        private Hand hand;
        private GameObject visualInstance;
        private Renderer[] renderers;
        private bool currentlyHighlighted = false;
        private Transform targetBone; // Awake에서 한 번 찾아서 캐싱, 매 프레임 재검색은 안 함
        private bool boneSearchAttempted = false;

        private void Awake()
        {
            hand = GetComponent<Hand>();

            if (visualPrefab != null)
            {
                visualInstance = Instantiate(visualPrefab, transform);
                visualInstance.transform.localPosition = Vector3.zero;
                visualInstance.transform.localRotation = Quaternion.Euler(visualRotationOffsetEuler);
                renderers = visualInstance.GetComponentsInChildren<Renderer>();
            }
        }

        private void LateUpdate()
        {
            if (visualInstance == null) return;

            // Play 모드 중에 값을 바꿔가며 바로 확인할 수 있도록 매 프레임 적용
            visualInstance.transform.localRotation = Quaternion.Euler(visualRotationOffsetEuler);

            // 뼈 참조를 아직 못 찾았으면(모델이 늦게 생성됐거나 이름이 아직 안 맞았을 수 있음) 계속 재시도
            if (recenterOnWrist && targetBone == null)
            {
                targetBone = FindDeepChild(visualInstance.transform, $"{targetBoneName}{handSuffix}");
                if (targetBone == null && !boneSearchAttempted)
                {
                    boneSearchAttempted = true;
                    Debug.LogWarning($"[HandVisual] {targetBoneName}{handSuffix} 뼈를 못 찾았습니다. targetBoneName/handSuffix 철자를 확인하세요.");
                }
            }

            // 매 프레임: "뼈가 지금 어디 있는지" vs "뼈가 있어야 할 곳(RightHand 원점 + 추가 오프셋)"을
            // 월드 좌표로 직접 비교해서 그 차이만큼 모델 전체를 옮김. 로컬 좌표를 계속 더하고 빼는
            // 대신 매번 절대 위치를 새로 계산하는 방식이라 누적 오차가 생길 수 없음.
            if (recenterOnWrist && targetBone != null)
            {
                Vector3 desiredBoneWorldPos = transform.TransformPoint(additionalOffset);
                Vector3 worldDelta = desiredBoneWorldPos - targetBone.position;
                visualInstance.transform.position += worldDelta;
            }
        }

        private Transform FindDeepChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform result = FindDeepChild(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private void Update()
        {
            if (visualInstance == null) return;

            bool shouldHide = false;
            if (hand.currentAttachedObjectInfo.HasValue && hand.currentAttachedObjectInfo.Value.interactable != null)
            {
                shouldHide = hand.currentAttachedObjectInfo.Value.interactable.hideHandOnAttach;
            }
            visualInstance.SetActive(!shouldHide);

            bool shouldHighlight = hand.hoveringInteractable != null;
            if (shouldHighlight != currentlyHighlighted)
            {
                currentlyHighlighted = shouldHighlight;
                ApplyMaterial(shouldHighlight ? highlightMaterial : normalMaterial);
            }
        }

        private void ApplyMaterial(Material mat)
        {
            if (mat == null || renderers == null) return;
            foreach (var r in renderers)
            {
                r.material = mat;
            }
        }

        void OnGUI()
        {
            if (!showDebugInfo) return;
            GUI.Label(new Rect(10, 330, 500, 24),
                $"HandVisual: targetBone={( targetBone != null ? targetBone.name : "못찾음")}  modelLocalPos={(visualInstance != null ? visualInstance.transform.localPosition.ToString("F3") : "-")}");
        }
    }
}
