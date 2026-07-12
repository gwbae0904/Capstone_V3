//======= Custom replacement for RenderModel.cs / ControllerHoverHighlight.cs =======
//
// 원본은 OpenVR 런타임에서 실제 컨트롤러의 3D 모델 파일을 그때그때 내려받아
// 렌더링하는 시스템이었습니다 (SteamVR_RenderModel). 우리는 OpenVR을 안 쓰니
// 이 방식 자체가 성립하지 않습니다.
//
// 대신 이 스크립트는 같은 "역할"만 가져옵니다:
//   1) 손 위치에 시각적으로 보이는 메시를 하나 붙여둔다 (지금은 임시 모델,
//      나중에 procedural 손 모델로 교체하면 됨)
//   2) 물체를 hover 중이면 하이라이트 머티리얼로 바뀐다
//   3) 물체를 잡은 동안은 Interactable.hideHandOnAttach 설정에 따라 손을 숨긴다
//
//=====================================================================

using UnityEngine;

namespace Valve.VR.InteractionSystem
{
    [RequireComponent(typeof(Hand))]
    public class HandVisual : MonoBehaviour
    {
        [Tooltip("손을 나타낼 임시/실제 3D 모델 프리팹. 지금은 구/캡슐 같은 placeholder를 넣어도 되고, 나중에 procedural 손 모델로 교체하면 됩니다.")]
        public GameObject visualPrefab;

        [Tooltip("평상시 머티리얼 (비워두면 프리팹의 원래 머티리얼 유지)")]
        public Material normalMaterial;
        [Tooltip("hover 중일 때 적용할 머티리얼")]
        public Material highlightMaterial;

        private Hand hand;
        private GameObject visualInstance;
        private Renderer[] renderers;
        private bool currentlyHighlighted = false;

        private void Awake()
        {
            hand = GetComponent<Hand>();

            if (visualPrefab != null)
            {
                visualInstance = Instantiate(visualPrefab, transform);
                visualInstance.transform.localPosition = Vector3.zero;
                visualInstance.transform.localRotation = Quaternion.identity;
                renderers = visualInstance.GetComponentsInChildren<Renderer>();
            }
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
    }
}
