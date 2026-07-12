//======= 임시 테스트용 드라이버 =======
//
// 아직 ArUco/IMU/포텐셔미터 파이프라인이 없는 지금 단계에서,
// 키보드/마우스로 Hand의 위치·회전·grab을 대신 조작해 데모 씬을 검증하기 위한 스크립트입니다.
//
// 나중에 실제 트래킹 파이프라인이 준비되면 이 스크립트를 끄고,
// 대신 ArUco 위치 -> transform.position, IMU 쿼터니언 -> transform.rotation,
// 포텐셔미터 curl -> hand.grabCurl 에 매 프레임 값을 대입해주는 스크립트로 교체하면 됩니다.
// (즉 이 스크립트가 하는 역할이 바로 "어댑터"가 할 일의 자리입니다.)
//
//=====================================================================

using UnityEngine;
using Valve.VR.InteractionSystem;

[RequireComponent(typeof(Hand))]
public class KeyboardHandDriver : MonoBehaviour
{
    [Header("이동/회전 속도")]
    public float moveSpeed = 1.0f;
    public float rotateSpeed = 90f;

    [Header("Grab 키")]
    public KeyCode grabKey = KeyCode.Space;

    private Hand hand;

    void Awake()
    {
        hand = GetComponent<Hand>();
    }

    void Update()
    {
        // WASD + Q/E: 이동 (카메라 기준이 아니라 월드 축 기준, 필요하면 나중에 바꿔도 됨)
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) move += Vector3.back;
        if (Input.GetKey(KeyCode.A)) move += Vector3.left;
        if (Input.GetKey(KeyCode.D)) move += Vector3.right;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;

        transform.position += move * moveSpeed * Time.deltaTime;

        // 화살표 키: 회전 (IMU 쿼터니언이 나중에 대신할 부분)
        float yaw = 0f, pitch = 0f;
        if (Input.GetKey(KeyCode.LeftArrow)) yaw -= 1f;
        if (Input.GetKey(KeyCode.RightArrow)) yaw += 1f;
        if (Input.GetKey(KeyCode.UpArrow)) pitch -= 1f;
        if (Input.GetKey(KeyCode.DownArrow)) pitch += 1f;

        transform.Rotate(Vector3.up, yaw * rotateSpeed * Time.deltaTime, Space.World);
        transform.Rotate(Vector3.right, pitch * rotateSpeed * Time.deltaTime, Space.Self);

        // Space bar를 누르고 있으면 grabCurl을 1로 (포텐셔미터가 나중에 대신할 부분)
        hand.grabCurl = Input.GetKey(grabKey) ? 1f : 0f;
    }
}
