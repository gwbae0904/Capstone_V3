#include <ESP32Servo.h>
#include "I2Cdev.h"
#include "MPU6050_6Axis_MotionApps20.h"
#include "Wire.h"

MPU6050 mpu;

// ==================================================
// 1. 기본 설정 (핀 번호 및 변수)
// ==================================================
const int NUM_FINGERS = 5;
const int ANALOG_MAX_VALUE = 4095;
const int MIN_VALID_CALIBRATION_RANGE = 50;
const unsigned long FINGER_CALIBRATION_TIME_MS = 3000;

// 가변저항 핀 (엄지, 검지, 중지, 약지, 소지)
const int potPins[NUM_FINGERS] = { 36, 39, 34, 35, 33 };

// 서보모터 핀
const int servoPins[NUM_FINGERS] = { 16, 17, 5, 18, 19 };
Servo hapticServos[NUM_FINGERS];

// ==================================================
// 2. 햅틱(서보) 관련 설정
// ==================================================
const int SERVO_MIN_ANGLE = 0;
const int SERVO_MAX_ANGLE = 180;
int servoAngles[NUM_FINGERS] = { 0, 0, 0, 0, 0 };

// 서보모터 회전 방향 반전
bool servoReverse[NUM_FINGERS] = { true, true, true, true, true };

// ==================================================
// 3. 손가락 초기 5초 자동 캘리브레이션
// ==================================================
int minRaw[NUM_FINGERS] = { 4095, 4095, 4095, 4095, 4095 };
int maxRaw[NUM_FINGERS] = { 0, 0, 0, 0, 0 };
bool calibrationComplete = false;

// 손가락별 센서 방향 반전 설정
bool flipCurl[NUM_FINGERS] = { true, true, false, false, false };

const bool DEBUG_MODE = false;

// ==================================================
// 4. MPU6050 (IMU) 변수
// ==================================================
bool dmpReady = false;
uint16_t packetSize;
uint8_t fifoBuffer[64];

Quaternion qCurrent;
Quaternion qBase;

float relativeQw = 1.0f;
float relativeQx = 0.0f;
float relativeQy = 0.0f;
float relativeQz = 0.0f;

// ==================================================
// 함수 선언
// ==================================================
const char* getFingerName(int fingerIndex);
void runInitialFingerCalibration();
void resetFingerCalibration();
int readRawAveraged(int pin);
float readCurlFromRaw(int raw, int minValue, int maxValue);
void handleSerialCommand();
void parseServoAngleCommand(String input);
int getActualServoAngle(int fingerIndex, int inputAngle);
void updateHapticServos();
void runTareCalibration();
void updateRelativeQuaternion();
void printUnityCSV(float curls[NUM_FINGERS], float qw, float qx, float qy, float qz);
void printDebug(int rawNow[NUM_FINGERS], float curls[NUM_FINGERS], float qw, float qx, float qy, float qz);

// ==================================================
// Setup (초기화)
// ==================================================
void setup() {
  Serial.begin(115200);
  Serial.setTimeout(5);

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  // 서보모터 초기화
  ESP32PWM::allocateTimer(0);
  ESP32PWM::allocateTimer(1);
  ESP32PWM::allocateTimer(2);
  ESP32PWM::allocateTimer(3);

  for (int i = 0; i < NUM_FINGERS; i++) {
    hapticServos[i].setPeriodHertz(50);
    hapticServos[i].attach(servoPins[i], 500, 2400);
    hapticServos[i].write(getActualServoAngle(i, servoAngles[i]));
  }

  // 전원이 켜지면 손가락 min/max를 3초 동안 자동 측정
  runInitialFingerCalibration();

  // MPU6050 초기화
  Wire.begin(21, 22);
  Wire.setClock(400000);
  mpu.initialize();

  if (!mpu.testConnection()) {
    Serial.println("MPU6050_CONNECTION_FAILED");
    while (true) {
      delay(100);
    }
  }

  uint8_t devStatus = mpu.dmpInitialize();
  mpu.setXGyroOffset(0);
  mpu.setYGyroOffset(0);
  mpu.setZGyroOffset(0);
  mpu.setZAccelOffset(0);

  if (devStatus == 0) {
    mpu.setDMPEnabled(true);
    dmpReady = true;
    packetSize = mpu.dmpGetFIFOPacketSize();

    // IMU 센서 안정화 후 기준 자세 설정
    delay(3000);
    runTareCalibration();
  } else {
    Serial.print("DMP_INITIALIZATION_FAILED,");
    Serial.println(devStatus);
  }
}

// ==================================================
// Loop (메인 반복문)
// ==================================================
void loop() {
  if (!dmpReady) {
    return;
  }

  // 1. 유니티 명령 수신
  handleSerialCommand();

  int rawNow[NUM_FINGERS];
  float curls[NUM_FINGERS];

  // 2. 현재 가변저항값 측정
  // 초기 5초 동안 저장한 min/max는 여기서 더 이상 갱신하지 않음
  for (int i = 0; i < NUM_FINGERS; i++) {
    int raw = readRawAveraged(potPins[i]);

    if (flipCurl[i]) {
      raw = ANALOG_MAX_VALUE - raw;
    }

    rawNow[i] = raw;
    curls[i] = readCurlFromRaw(raw, minRaw[i], maxRaw[i]);
  }

  // 3. 서보모터에 각도 적용
  updateHapticServos();

  // 4. 새 IMU 패킷이 있으면 상대 Quaternion 갱신
  if (mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
    updateRelativeQuaternion();
  }

  // 5. 유니티로 센서 데이터 전송
  if (DEBUG_MODE) {
    printDebug(rawNow, curls, relativeQw, relativeQx, relativeQy, relativeQz);
  } else {
    printUnityCSV(curls, relativeQw, relativeQx, relativeQy, relativeQz);
  }

  delay(10);
}

// ==================================================
// 가변저항 정규화 (0.0 ~ 1.0)
// ==================================================
float readCurlFromRaw(int raw, int minValue, int maxValue) {
  int range = maxValue - minValue;

  // 5초 동안 충분히 쥐었다 펴지 않아 측정 범위가 너무 작으면 0.0 반환
  if (range < MIN_VALID_CALIBRATION_RANGE) {
    return 0.0f;
  }

  float normalized = (float)(raw - minValue) / (float)range;
  return constrain(normalized, 0.0f, 1.0f);
}

// ==================================================
// 유니티 명령 파싱
// ==================================================
void handleSerialCommand() {
  if (Serial.available() <= 0) {
    return;
  }

  String input = Serial.readStringUntil('\n');
  input.trim();

  if (input.length() == 0) {
    return;
  }

  // IMU 영점 재조정
  if (input == "t" || input == "T") {
    runTareCalibration();
    return;
  }

  // 손가락 min/max 3초 재캘리브레이션
  if (input == "r" || input == "R" || input == "c" || input == "C") {
    runInitialFingerCalibration();
    return;
  }

  // 손가락별 서보 각도 명령: H,각도1,각도2,각도3,각도4,각도5
  if (input.startsWith("H,") || input.startsWith("h,")) {
    parseServoAngleCommand(input);
    return;
  }
}

void parseServoAngleCommand(String input) {
  int firstComma = input.indexOf(',');
  if (firstComma == -1) {
    return;
  }

  String remaining = input.substring(firstComma + 1);

  for (int i = 0; i < NUM_FINGERS; i++) {
    int commaIndex = remaining.indexOf(',');
    String valueString;

    if (i < NUM_FINGERS - 1) {
      if (commaIndex == -1) {
        return;
      }

      valueString = remaining.substring(0, commaIndex);
      remaining = remaining.substring(commaIndex + 1);
    } else {
      valueString = remaining;
    }

    valueString.trim();

    if (valueString.length() == 0) {
      return;
    }

    servoAngles[i] = constrain(
      valueString.toInt(),
      SERVO_MIN_ANGLE,
      SERVO_MAX_ANGLE
    );
  }
}

// ==================================================
// 서보 방향 및 구동
// ==================================================
int getActualServoAngle(int fingerIndex, int inputAngle) {
  inputAngle = constrain(inputAngle, SERVO_MIN_ANGLE, SERVO_MAX_ANGLE);

  if (servoReverse[fingerIndex]) {
    return SERVO_MAX_ANGLE - inputAngle;
  }

  return inputAngle;
}

void updateHapticServos() {
  for (int i = 0; i < NUM_FINGERS; i++) {
    hapticServos[i].write(
      getActualServoAngle(i, servoAngles[i])
    );
  }
}

// ==================================================
// 손가락 캘리브레이션 및 ADC
// ==================================================
void runInitialFingerCalibration() {
  calibrationComplete = false;
  resetFingerCalibration();

  Serial.println("CALIBRATION_START");
  Serial.println("3초 동안 손가락을 반복해서 쥐었다 펴세요.");

  unsigned long startTime = millis();

  while (millis() - startTime < FINGER_CALIBRATION_TIME_MS) {
    for (int i = 0; i < NUM_FINGERS; i++) {
      int raw = readRawAveraged(potPins[i]);

      if (flipCurl[i]) {
        raw = ANALOG_MAX_VALUE - raw;
      }

      // 처음 3초 동안에만 손가락별 최소값과 최대값 갱신
      if (raw < minRaw[i]) {
        minRaw[i] = raw;
      }

      if (raw > maxRaw[i]) {
        maxRaw[i] = raw;
      }
    }

    delay(5);
  }

  calibrationComplete = true;

  // 실행 중 재캘리브레이션한 경우 밀려 있는 IMU 패킷 제거
  if (dmpReady) {
    mpu.resetFIFO();
  }

  Serial.println("CALIBRATION_DONE");
}

void resetFingerCalibration() {
  for (int i = 0; i < NUM_FINGERS; i++) {
    minRaw[i] = ANALOG_MAX_VALUE;
    maxRaw[i] = 0;
  }
}

int readRawAveraged(int pin) {
  long sum = 0;

  for (int i = 0; i < 3; i++) {
    sum += analogRead(pin);
    delayMicroseconds(200);
  }

  return sum / 3;
}

// ==================================================
// IMU 영점(Tare) 및 회전 연산
// ==================================================
void updateRelativeQuaternion() {
  mpu.dmpGetQuaternion(&qCurrent, fifoBuffer);

  // qBase의 역Quaternion과 qCurrent를 곱함
  float q1w = qBase.w;
  float q1x = -qBase.x;
  float q1y = -qBase.y;
  float q1z = -qBase.z;

  float q2w = qCurrent.w;
  float q2x = qCurrent.x;
  float q2y = qCurrent.y;
  float q2z = qCurrent.z;

  relativeQw = q1w * q2w - q1x * q2x - q1y * q2y - q1z * q2z;
  relativeQx = q1w * q2x + q1x * q2w + q1y * q2z - q1z * q2y;
  relativeQy = q1w * q2y - q1x * q2z + q1y * q2w + q1z * q2x;
  relativeQz = q1w * q2z + q1x * q2y - q1y * q2x + q1z * q2w;

  float magnitude = sqrt(
    relativeQw * relativeQw +
    relativeQx * relativeQx +
    relativeQy * relativeQy +
    relativeQz * relativeQz
  );

  if (magnitude > 0.0f) {
    relativeQw /= magnitude;
    relativeQx /= magnitude;
    relativeQy /= magnitude;
    relativeQz /= magnitude;
  }
}

void runTareCalibration() {
  mpu.resetFIFO();
  delay(50);

  unsigned long startTime = millis();
  float sumW = 0.0f;
  float sumX = 0.0f;
  float sumY = 0.0f;
  float sumZ = 0.0f;
  int sampleCount = 0;

  while (millis() - startTime < 1500) {
    if (mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
      Quaternion tempQ;
      mpu.dmpGetQuaternion(&tempQ, fifoBuffer);

      sumW += tempQ.w;
      sumX += tempQ.x;
      sumY += tempQ.y;
      sumZ += tempQ.z;
      sampleCount++;
    }

    delay(5);
  }

  if (sampleCount > 0) {
    qBase.w = sumW / sampleCount;
    qBase.x = sumX / sampleCount;
    qBase.y = sumY / sampleCount;
    qBase.z = sumZ / sampleCount;

    float mag = sqrt(
      qBase.w * qBase.w +
      qBase.x * qBase.x +
      qBase.y * qBase.y +
      qBase.z * qBase.z
    );

    if (mag > 0.0f) {
      qBase.w /= mag;
      qBase.x /= mag;
      qBase.y /= mag;
      qBase.z /= mag;
    } else {
      qBase.w = 1.0f;
      qBase.x = 0.0f;
      qBase.y = 0.0f;
      qBase.z = 0.0f;
    }
  } else {
    qBase.w = 1.0f;
    qBase.x = 0.0f;
    qBase.y = 0.0f;
    qBase.z = 0.0f;
  }

  relativeQw = 1.0f;
  relativeQx = 0.0f;
  relativeQy = 0.0f;
  relativeQz = 0.0f;

  mpu.resetFIFO();
}

// ==================================================
// 통신 및 디버그
// ==================================================
void printUnityCSV(
  float curls[NUM_FINGERS],
  float qw,
  float qx,
  float qy,
  float qz
) {
  for (int i = 0; i < NUM_FINGERS; i++) {
    Serial.print(curls[i], 4);
    Serial.print(",");
  }

  Serial.print(qw, 4);
  Serial.print(",");
  Serial.print(qx, 4);
  Serial.print(",");
  Serial.print(qy, 4);
  Serial.print(",");
  Serial.println(qz, 4);
}

void printDebug(
  int rawNow[NUM_FINGERS],
  float curls[NUM_FINGERS],
  float qw,
  float qx,
  float qy,
  float qz
) {
  static unsigned long lastDebugPrint = 0;

  if (millis() - lastDebugPrint < 1000) {
    return;
  }

  lastDebugPrint = millis();

  Serial.println("=========================================");

  for (int i = 0; i < NUM_FINGERS; i++) {
    Serial.print("F");
    Serial.print(i);
    Serial.print(" ");
    Serial.print(getFingerName(i));
    Serial.print(" | raw=");
    Serial.print(rawNow[i]);
    Serial.print(" | min=");
    Serial.print(minRaw[i]);
    Serial.print(" | max=");
    Serial.print(maxRaw[i]);
    Serial.print(" | curl=");
    Serial.print(curls[i], 3);
    Serial.print(" | servoAngle=");
    Serial.println(servoAngles[i]);
  }

  Serial.print("Quaternion | qw=");
  Serial.print(qw, 3);
  Serial.print(" | qx=");
  Serial.print(qx, 3);
  Serial.print(" | qy=");
  Serial.print(qy, 3);
  Serial.print(" | qz=");
  Serial.println(qz, 3);
  Serial.println("=========================================");
}

const char* getFingerName(int fingerIndex) {
  switch (fingerIndex) {
    case 0:
      return "Thumb ";
    case 1:
      return "Index ";
    case 2:
      return "Middle";
    case 3:
      return "Ring  ";
    case 4:
      return "Pinky ";
    default:
      return "Unknown";
  }
}