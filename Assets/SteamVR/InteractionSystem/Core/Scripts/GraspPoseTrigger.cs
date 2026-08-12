// GraspPoseTrigger.cs
//
// 물체(Interactable)를 잡는 순간, 장갑 모델의 grasp pose 애니메이터
// (vr_glove_graspPoses.controller)를 이 물체에 맞는 상태로 즉시 전환합니다.
//
// vr_glove_graspPoses의 트리거(AnimationState) 구조는 "한 트리거로 다음 상태로
// 순환"하는 데모용 구조라 "이 물체는 무조건 이 포즈"처럼 특정 상태를 정확히
// 고르기 애매합니다. 그래서 트리거 대신 Animator.Play(상태이름)으로 원하는
// 상태에 바로 점프시킵니다 (컨트롤러의 트랜지션 구조 자체는 안 건드림).
//
// 사용법: 잡을 수 있는 물체(Interactable이 붙은 오브젝트, 또는 그 오브젝트의
// 자식)에 이 스크립트를 붙이고, Pose State Name에 "Rest" / "SphereGrab" /
// "StickGrab" / "pinchGrab" 중 이 물체에 맞는 걸 골라 넣으면 됩니다.
//
// [수정 이력]
// - [RequireComponent(typeof(Interactable))]를 제거함.
//   원인: 이 데모 씬은 Interactable이 보통 부모 오브젝트(Throwable (...))에
//   붙어있고, 실제 메시가 있는 자식(Cube/Sphere)에는 없는 구조인 경우가 많음.
//   RequireComponent를 쓴 채로 이 스크립트를 자식 오브젝트에 붙이면, Unity가
//   "컴포넌트가 없다"고 판단해서 자식에 새 Interactable을 자동으로 만들어버림.
//   그 결과 부모의 진짜 Interactable(Throwable.cs가 참조하는 것)과 자식의
//   가짜 Interactable(방금 자동 생성된, 아무 데도 연결 안 된 빈 컴포넌트)이
//   동시에 존재하게 되고, Hand.UpdateHovering()의 GetComponentInParent가
//   자기 자신에 더 가까운 가짜 쪽을 먼저 찾아버려서 hover/grab 흐름 전체가
//   엉뚱한 인스턴스로 새는 문제가 있었음.
//   -> GetComponentInParent<Interactable>()로 직접 찾도록 바꾸고, 못 찾으면
//   자동 생성하지 않고 명확한 에러를 남기도록 변경. 이러면 이 스크립트를
//   자식/부모 어디에 붙여도 안전하고, 실수로 중복 생성되는 일도 없음.
// - Haptic Stop Value 필드 추가. 원인: 이 프로젝트의 실제 하드웨어(Lucas
//   glove 방식)는 모터가 회전축과 수직으로 붙어있어서, 미리 원하는 각도로
//   돌려두면 손가락 회전축의 나사가 모터 축에 닿는 순간 물리적으로 더 이상
//   못 굽혀지는 방식으로 저항을 만듦. 즉 서보에 보내는 값이 "얼마나 세게
//   쥐는지"가 아니라 "이 물체를 잡을 때 손가락이 어디까지 굽혀지도록
//   허용할지(=물체 크기)"를 뜻함. 물체마다 크기가 다르므로 이 값도 물체별로
//   다르게 설정 가능하도록 여기(GraspPoseTrigger)에 필드로 둠.
//   (실제 전송은 SerialGloveReceiver.cs에서, hover/grab 중인 물체의 이 값을
//   읽어와서 처리함)
// - 손가락별 정지값을 0~1000 추상값 대신 0~180도(서보 실제 각도)로 직접
//   입력하도록 변경. 튜닝할 때 "1000분의 몇"보다 "몇 도"가 훨씬 직관적이라
//   실측 조정이 쉬워짐. 시리얼 프로토콜(아두이노 쪽)은 그대로 0~1000을
//   받으므로, GetHapticStopValues()에서 아두이노의 각도 변환식
//   (angle = 180 - value/1000*180)의 역산으로 내부적으로 변환해서 반환함.
//   기존 튜닝값(100/600/1000/600/1000, 0~1000 기준)은 실제 서보 각도로 보면
//   162°/72°/0°/72°/0°였으므로, 새 기본값도 그와 동일한 각도로 맞춰둠.
// - Custom Grab Threshold 필드 추가. 물체마다 필요한 grab curl 기준값이
//   다를 수 있어서(예: 얇은 물체는 더 많이 굽혀야 "잡았다"고 볼 수 있음),
//   Hand의 전역 Grab Threshold 대신 물체별로 재정의할 수 있게 함.
//   Use Custom Grab Threshold 체크박스로 켜고 끄는 방식 (처음엔 -1을
//   "설정 안 함"으로 쓰는 방식이었는데, 체크박스가 훨씬 명확해서 이렇게 변경)

using UnityEngine;
using Valve.VR.InteractionSystem;

public class GraspPoseTrigger : MonoBehaviour
{
    [Tooltip("이 물체를 잡았을 때 재생할 애니메이터 상태 이름")]
    public string poseStateName = "SphereGrab";

    [Tooltip("잡았던 물체를 놓았을 때 되돌아갈 상태 이름 (보통 Rest)")]
    public string releasePoseStateName = "Rest";

    [Tooltip("Animator의 레이어 인덱스 (보통 0이면 됨, Base Layer)")]
    public int animatorLayer = 0;

    [Tooltip("상태 전환에 걸리는 시간(초). 0이면 즉시 전환")]
    public float transitionDuration = 0.1f;

    [Header("햅틱 (손가락별 모터 정지 각도, 실제 서보 각도 기준)")]
    [Tooltip("이 물체를 잡을 때(또는 hover만 할 때 미리) 각 손가락이 여기까지만 굽혀지도록 서보를 세팅할 각도(0~180도). " +
             "물체를 실제로 손으로 쥐어보면서 손가락별로 값을 조정하세요. 각도가 작을수록 더 많이 굽혀짐(0도=완전히 굽힘), 클수록 일찍 멈춤(180도=편 상태)")]
    [Range(0, 180)] public int thumbStopAngle = 162;
    [Range(0, 180)] public int indexStopAngle = 72;
    [Range(0, 180)] public int middleStopAngle = 0;
    [Range(0, 180)] public int ringStopAngle = 72;
    [Range(0, 180)] public int pinkyStopAngle = 0;

    [Header("Grab 판정")]
    [Tooltip("켜면 이 물체는 Hand의 기본 Grab Threshold 대신 아래 커스텀 값을 사용합니다.")]
    public bool useCustomGrabThreshold = false;
    [Tooltip("Use Custom Grab Threshold가 켜져 있을 때 사용할 값 (0~1)")]
    [Range(0f, 1f)]
    public float customGrabThreshold = 0.7f;

    // 아두이노는 0~1000 값을 받아 angle = 180 - value/1000*180 으로 서보를 돌림.
    // 여기서는 위 각도(0~180)를 그 역산 공식으로 0~1000 값으로 바꿔서 넘겨줌
    // (시리얼 프로토콜/아두이노 펌웨어는 그대로 두고, Unity 쪽 입력 단위만 바꾼 것)
    private int AngleToWireValue(int angleDeg)
    {
        return Mathf.RoundToInt((180 - angleDeg) / 180f * 1000f);
    }

    // SerialGloveReceiver가 순서대로 꺼내 쓰기 편하도록 배열로 제공 (엄지,검지,중지,약지,소지 순서).
    // 반환값은 기존과 동일하게 0~1000 wire 프로토콜 값 (내부적으로 각도에서 변환됨)
    public int[] GetHapticStopValues()
    {
        return new int[]
        {
            AngleToWireValue(thumbStopAngle),
            AngleToWireValue(indexStopAngle),
            AngleToWireValue(middleStopAngle),
            AngleToWireValue(ringStopAngle),
            AngleToWireValue(pinkyStopAngle),
        };
    }

    private Interactable interactable;

    void Start()
    {
        // 자기 자신 또는 부모 쪽에 이미 있는 Interactable을 찾음.
        // 새로 만들지 않음 - 이 씬 구조상 Interactable이 부모에 있는 경우가
        // 흔해서, 자동 생성하면 중복 컴포넌트 문제가 생기기 때문.
        interactable = GetComponentInParent<Interactable>();

        if (interactable == null)
        {
            Debug.LogError($"[GraspPoseTrigger] {gameObject.name}: 이 오브젝트나 부모 어디에도 Interactable이 없습니다. " +
                            "GraspPoseTrigger는 Interactable이 이미 붙어있는 오브젝트(보통 Throwable이 붙은 부모)에 붙여야 합니다.");
            return;
        }

        interactable.onAttachedToHand += OnAttached;
        interactable.onDetachedFromHand += OnDetached;
    }

    void OnDestroy()
    {
        if (interactable != null)
        {
            interactable.onAttachedToHand -= OnAttached;
            interactable.onDetachedFromHand -= OnDetached;
        }
    }

    private void OnAttached(Hand hand)
    {
        PlayPoseOn(hand, poseStateName);
    }

    private void OnDetached(Hand hand)
    {
        PlayPoseOn(hand, releasePoseStateName);
    }

    private void PlayPoseOn(Hand hand, string stateName)
    {
        if (hand == null) return;

        Animator animator = hand.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"[GraspPoseTrigger] {hand.name} 아래에서 Animator를 못 찾았습니다.");
            return;
        }

        if (transitionDuration <= 0f)
            animator.Play(stateName, animatorLayer);
        else
            animator.CrossFadeInFixedTime(stateName, transitionDuration, animatorLayer);
    }
}
