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
