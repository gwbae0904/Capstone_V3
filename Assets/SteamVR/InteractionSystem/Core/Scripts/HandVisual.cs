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
//   5) HoverPoint / ObjectAttachmentPoint도 모델의 특정 뼈(보통 손바닥 근처)를
//      따라가도록 매 프레임 재정렬한다
//
// [수정 이력]
// - 4번 보정을 Awake()에서 딱 한 번만 계산하던 방식에서 매 프레임(LateUpdate)
//   재계산하는 방식으로 변경. (엄지 등 엉뚱한 지점이 회전축처럼 보이는 문제 해결)
// - 위 수정으로 손 모델의 시각적 위치 자체가 바뀌면서, 예전에 "틀어진 위치" 기준으로
//   사람이 손으로 튜닝해뒀던 HoverPoint/ObjectAttachmentPoint 고정 위치가 더 이상
//   안 맞게 됨 (둘은 같은 버그의 다른 증상이었을 뿐, 서로 독립적으로 고칠 수 없음).
// - 5번(자동 정렬)은 시도해볼 수 있는 옵션으로 남겨두되, 기본 권장 경로는:
//   Hover Point To Align / Attachment Point To Align을 비워두고(자동 정렬 끔),
//   지금(올바르게 고쳐진) 손 모양을 Scene 뷰로 직접 보면서 HoverPoint/
//   ObjectAttachmentPoint 위치를 손으로 다시 맞추는 것. 이건 버그가 아니라
//   미적/체감적 튜닝이라 자동 계산보다 직접 눈으로 맞추는 게 더 정확함.

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

        [Header("모델 전체 정렬 (마커 위치 = 손의 어느 부위인지)")]
        [Tooltip("이 모델의 회전 중심(=마커 위치가 대응될 지점)을 특정 뼈에 맞출지 여부.")]
        public bool recenterOnWrist = true;
        [Tooltip("어느 뼈를 기준점으로 삼을지. 접미사(_r/_l)는 자동으로 붙습니다. 예: wrist(손목), root(손 전체 기준), finger_middle_meta(손등 근처), finger_thumb_0(엄지 시작점) 등")]
        public string targetBoneName = "wrist";
        [Tooltip("오른손 모델이면 _r, 왼손 모델이면 _l")]
        public string handSuffix = "_r";
        [Tooltip("뼈 기준점에서 추가로 더 밀고 싶은 만큼(로컬 기준, 미터 단위)")]
        public Vector3 additionalOffset = Vector3.zero;
        [Tooltip("모델 자체의 원래 조형(bind pose) 각도 보정. X/Y/Z 오일러 각도(도)")]
        public Vector3 visualRotationOffsetEuler = Vector3.zero;

        [Header("Hover / 잡기 기준점 (손바닥 근처 뼈를 따라가게)")]
        [Tooltip("이 오브젝트를 켜두면, 물체 hover 판정용 지점(HoverPoint)도 아래 뼈를 따라 매 프레임 이동합니다. RightHand의 HoverPoint 자식 오브젝트를 여기 연결하세요. 비워두면 안 건드립니다.")]
        public Transform hoverPointToAlign;
        [Tooltip("잡은 물체가 스냅될 지점(ObjectAttachmentPoint)도 같이 이동시키려면 여기 연결하세요. 비워두면 안 건드립니다.")]
        public Transform attachmentPointToAlign;
        [Header("디버그")]
        public bool showDebugInfo = true;

        private Hand hand;
        private GameObject visualInstance;
        private Renderer[] renderers;
        private bool currentlyHighlighted = false;
        private Transform targetBone;
        private Vector3 lastWorldDelta;
        private Vector3 hoverLocalPos, attachLocalPos;
        private Quaternion hoverLocalRot = Quaternion.identity, attachLocalRot = Quaternion.identity;
        private bool boneSearchAttempted = false;

        private void Awake()
        {
            hand = GetComponent<Hand>();

            if (visualPrefab != null)
            {
                visualInstance = Instantiate(visualPrefab, transform);
                visualInstance.transform.localPosition = Vector3.zero;

                // 캡처는 반드시 "회전 오프셋이 전혀 없는(identity)" 기준으로 해야 함.
                // GitHub 원본에서 ObjectAttachmentPoint/HoverPoint를 튜닝할 때는 이 회전
                // 오프셋(Visual Rotation Offset Euler, 나중에 악수 포즈 고치려고 추가한 것)이
                // 존재하지 않았으므로, 캡처 시점에 Inspector에 어떤 값이 들어있든 무시하고
                // identity로 잠깐 맞춘 뒤 캡처해야 원본 관계와 정확히 일치함.
                // (Play를 시작할 때 이 필드에 이미 0이 아닌 값이 들어있으면, 그 값 기준으로
                // 캡처되어버려서 원본 관계가 어긋나는 버그가 있었음 - 여기서 수정)
                visualInstance.transform.localRotation = Quaternion.identity;
                renderers = visualInstance.GetComponentsInChildren<Renderer>();

                if (hoverPointToAlign != null)
                {
                    hoverLocalPos = visualInstance.transform.InverseTransformPoint(hoverPointToAlign.position);
                    hoverLocalRot = Quaternion.Inverse(visualInstance.transform.rotation) * hoverPointToAlign.rotation;
                }
                if (attachmentPointToAlign != null)
                {
                    attachLocalPos = visualInstance.transform.InverseTransformPoint(attachmentPointToAlign.position);
                    attachLocalRot = Quaternion.Inverse(visualInstance.transform.rotation) * attachmentPointToAlign.rotation;
                }

                // 캡처가 끝난 뒤에야 실제 회전 오프셋을 적용 (LateUpdate에서도 매 프레임 다시 적용되지만,
                // 첫 프레임 렌더링 전에도 바로 반영되도록 여기서도 한 번 적용)
                visualInstance.transform.localRotation = Quaternion.Euler(visualRotationOffsetEuler);
            }
        }

        private void LateUpdate()
        {
            if (visualInstance == null) return;

            visualInstance.transform.localRotation = Quaternion.Euler(visualRotationOffsetEuler);

            // ----- 1) 모델 전체를 손목(등) 기준으로 정렬 -----
            if (recenterOnWrist && targetBone == null)
            {
                targetBone = FindDeepChild(visualInstance.transform, $"{targetBoneName}{handSuffix}");
                if (targetBone == null && !boneSearchAttempted)
                {
                    boneSearchAttempted = true;
                    Debug.LogWarning($"[HandVisual] {targetBoneName}{handSuffix} 뼈를 못 찾았습니다.");
                }
            }
            Vector3 worldDelta = Vector3.zero;
            if (recenterOnWrist && targetBone != null)
            {
                Vector3 desiredBoneWorldPos = transform.TransformPoint(additionalOffset);
                worldDelta = desiredBoneWorldPos - targetBone.position;
                visualInstance.transform.position += worldDelta;
                lastWorldDelta = worldDelta;
            }

            // ----- 2) HoverPoint / ObjectAttachmentPoint가 모델을 "가상으로" 따라가게 함 -----
            // 실제로 부모를 모델로 바꿔버리면, 물체 잡을 때 손 모델을 숨기는 기능
            // (hideHandOnAttach)이 켜져 있는 경우 ObjectAttachmentPoint까지 같이 꺼져서
            // 거기 붙어있는 "잡은 물체"까지 안 보이게 될 위험이 있음. 그래서 진짜
            // 부모-자식으로 만들지는 않고, 최초 1회 "모델 기준 상대 위치+회전"을
            // 캡처해두고, 매 프레임 그 관계를 그대로 재현함(부모-자식과 동일한 효과,
            // 계층구조는 안 건드림).
            if (hoverPointToAlign != null)
            {
                hoverPointToAlign.position = visualInstance.transform.TransformPoint(hoverLocalPos);
                hoverPointToAlign.rotation = visualInstance.transform.rotation * hoverLocalRot;
            }
            if (attachmentPointToAlign != null)
            {
                attachmentPointToAlign.position = visualInstance.transform.TransformPoint(attachLocalPos);
                attachmentPointToAlign.rotation = visualInstance.transform.rotation * attachLocalRot;
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
                $"HandVisual: targetBone={(targetBone != null ? targetBone.name : "못찾음")}  lastWorldDelta={lastWorldDelta:F3}");
        }
    }
}
