// FingerCurlAnimator.cs
//
// SerialGloveReceiver가 받은 curl 값(0=편손, 1=완전히 굽힘) 5개를,
// 장갑 3D 모델(vr_glove_right_model_slim 등, Valve 표준 손 스켈레톤 명명규칙)의
// 실제 손가락 뼈에 적용해서 손가락이 굽어 보이게 합니다.
//
// 장갑 모델은 HandVisual.cs가 런타임에 Instantiate 하기 때문에,
// 이 스크립트는 에디터에서 뼈를 미리 드래그 연결하는 대신,
// 이름으로 찾아서 자동으로 연결합니다. (모델이 늦게 생겨도 몇 프레임 안에 자동 인식)
//
// [수정 이력]
// - meta/j0/j1/j2 네 관절에 동일한 curl delta를 주던 방식에서,
//   관절별로 다른 비율(Ratio)을 곱하는 방식으로 변경.
//   원인: meta는 부모 뼈라서 회전하면 자식 뼈(j0,j1,j2) 전체가 통째로 따라 움직임.
//   Valve 표준 스켈레톤에서 meta는 원래 손가락 벌림(abduction)용 뼈라 curl에는
//   기본적으로 거의 관여하면 안 되는데, 기존 코드는 여기에도 굽힘 각도를 그대로
//   줘서 MCP(중수지관절) 부분이 이중으로 꺾여 과도하게 굽어 보이는 문제가 있었음.
//   -> metaRatio를 기본 0으로 두고, j0/j1/j2에 관절별 비율을 따로 줘서 해결.
// - Hand 참조(handForGrabCheck)를 추가해서, 물체를 잡고 있는 동안은 curl 기반
//   덮어쓰기를 건너뛰도록 함. 원인: 물체를 잡아도 이 스크립트가 매 LateUpdate마다
//   무조건 curl 값으로 손가락 뼈를 덮어써서, 장갑 프리팹의 grasp pose Animator
//   (vr_glove_graspPoses)가 물체 모양에 맞춰 잡아둔 자세가 항상 씹히는 문제가 있었음.
//   -> 잡고 있는 동안은 이 스크립트가 손을 떼고, Animator가 자세를 그대로 유지하게 함.
//   (단, 물체 종류별로 어떤 grasp pose 클립을 재생할지 트리거하는 로직은 별도이며
//   Interactable.cs/Throwable.cs 쪽에 있을 것으로 예상 - 아직 미확인)

using UnityEngine;
using Valve.VR.InteractionSystem;

public class FingerCurlAnimator : MonoBehaviour
{
    [Header("데이터 소스")]
    [Tooltip("curls[] 배열(엄지,검지,중지,약지,소지 순서)을 제공하는 컴포넌트")]
    public SerialGloveReceiver serialReceiver;

    [Header("Grab 중 자세 처리")]
    [Tooltip("물체를 잡고 있는 동안은 curl 값으로 손가락을 덮어쓰지 않고, 장갑 프리팹의 grasp pose Animator(vr_glove_graspPoses)에게 자세를 맡깁니다. 보통 SerialGloveReceiver의 Target Hand와 같은 오브젝트를 연결하면 됩니다. 비워두면 항상 curl 값을 반영합니다.")]
    public Hand handForGrabCheck;

    [Header("뼈 이름 접미사")]
    [Tooltip("오른손 모델이면 _r, 왼손 모델이면 _l")]
    public string handSuffix = "_r";

    [Header("손가락별 최대 굽힘 각도 (완전히 curl=1일 때, 관절당 도 — 아래 관절별 비율과 곱해서 최종 적용됨)")]
    public float thumbMaxAngle = 60f;
    public float otherFingerMaxAngle = 80f;

    [Header("관절별 굽힘 비율 (0~1, Max Angle 대비)")]
    [Tooltip("meta는 Valve 스켈레톤에서 원래 손가락 벌림(abduction)용 뼈라 curl엔 기본 0에 가깝게 두는 걸 추천. 필요하면 0.1~0.2 정도만 살짝 줘서 손등 볼륨감만 살리는 용도로 사용")]
    [Range(0f, 1f)] public float metaRatio = 0f;
    [Tooltip("MCP(중수지관절) 굽힘 - 보통 가장 크게 굽는 관절")]
    [Range(0f, 1f)] public float j0Ratio = 1.0f;
    [Tooltip("PIP(근위지절간관절)")]
    [Range(0f, 1f)] public float j1Ratio = 0.9f;
    [Tooltip("DIP(원위지절간관절) - 보통 PIP보다 덜 굽음")]
    [Range(0f, 1f)] public float j2Ratio = 0.7f;

    [Tooltip("굽히는 국소 회전축. 모델 방향에 따라 안 맞으면 부호/축을 바꿔보세요")]
    public Vector3 curlAxis = Vector3.right;

    private FingerJointSet[] fingers; // 0:엄지 1:검지 2:중지 3:약지 4:소지
    private bool bonesFound = false;

    private class FingerJointSet
    {
        public Transform meta;   // 검지~소지만 존재 (엄지는 null)
        public Transform j0, j1, j2;
        public Quaternion bindMeta, bind0, bind1, bind2;
    }

    void LateUpdate()
    {
        if (!bonesFound)
        {
            TryFindBones();
            if (!bonesFound) return; // 모델이 아직 인스턴스화 안 됐으면 다음 프레임에 재시도
        }

        if (serialReceiver == null) return;

        // 물체를 잡고 있는 동안은 grasp pose Animator에게 자세를 맡기고 이 스크립트는 쉼
        bool isGrabbingObject = handForGrabCheck != null && handForGrabCheck.currentAttachedObject != null;
        if (isGrabbingObject) return;

        for (int i = 0; i < fingers.Length; i++)
        {
            if (fingers[i] == null) continue;
            float curl = (serialReceiver.curls != null && i < serialReceiver.curls.Length) ? serialReceiver.curls[i] : 0f;
            float maxAngle = (i == 0) ? thumbMaxAngle : otherFingerMaxAngle;

            var f = fingers[i];

            Quaternion deltaMeta = Quaternion.AngleAxis(curl * maxAngle * metaRatio, curlAxis);
            Quaternion delta0 = Quaternion.AngleAxis(curl * maxAngle * j0Ratio, curlAxis);
            Quaternion delta1 = Quaternion.AngleAxis(curl * maxAngle * j1Ratio, curlAxis);
            Quaternion delta2 = Quaternion.AngleAxis(curl * maxAngle * j2Ratio, curlAxis);

            if (f.meta != null) f.meta.localRotation = f.bindMeta * deltaMeta;
            if (f.j0 != null) f.j0.localRotation = f.bind0 * delta0;
            if (f.j1 != null) f.j1.localRotation = f.bind1 * delta1;
            if (f.j2 != null) f.j2.localRotation = f.bind2 * delta2;
        }
    }

    private void TryFindBones()
    {
        string[] names = { "thumb", "index", "middle", "ring", "pinky" };
        fingers = new FingerJointSet[5];
        int foundCount = 0;

        for (int i = 0; i < names.Length; i++)
        {
            Transform j0 = FindDeepChild(transform, $"finger_{names[i]}_0{handSuffix}");
            Transform j1 = FindDeepChild(transform, $"finger_{names[i]}_1{handSuffix}");
            Transform j2 = FindDeepChild(transform, $"finger_{names[i]}_2{handSuffix}");
            Transform meta = FindDeepChild(transform, $"finger_{names[i]}_meta{handSuffix}");

            if (j0 == null && j1 == null && j2 == null)
                continue; // 이 손가락은 아직(또는 끝내) 못 찾음

            var set = new FingerJointSet
            {
                meta = meta,
                j0 = j0,
                j1 = j1,
                j2 = j2,
                bindMeta = meta != null ? meta.localRotation : Quaternion.identity,
                bind0 = j0 != null ? j0.localRotation : Quaternion.identity,
                bind1 = j1 != null ? j1.localRotation : Quaternion.identity,
                bind2 = j2 != null ? j2.localRotation : Quaternion.identity,
            };
            fingers[i] = set;
            foundCount++;
        }

        if (foundCount >= 4) // 5개 다 못 찾아도(엄지 구조가 달라서) 대부분 찾았으면 시작
        {
            bonesFound = true;
            Debug.Log($"[FingerCurlAnimator] 손가락 뼈 {foundCount}/5개 인식 완료, curl 애니메이션 시작");
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

    void OnGUI()
    {
        GUI.Label(new Rect(10, 300, 500, 24), $"FingerCurlAnimator: 뼈 인식됨={bonesFound}");
    }
}
