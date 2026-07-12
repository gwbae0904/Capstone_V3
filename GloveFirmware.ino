#include <limits.h>
#include <ESP32Servo.h>

#include "I2Cdev.h"
#include "MPU6050_6Axis_MotionApps20.h"
#include "Wire.h"

MPU6050 mpu;

// ===============================
// 기본 설정
// ===============================

const int NUM_FINGERS = 5;
const int ANALOG_MAX_VALUE = 4095;

// 가변저항 핀: 엄지, 검지, 중지, 약지, 소지
const int potPins[NUM_FINGERS] = {
  36, 39, 34, 35, 32
};

// 서보 핀: 엄지, 검지, 중지, 약지, 소지
const int servoPins[NUM_FINGERS] = {
  16, 17, 5, 18, 19
};

Servo hapticServos[NUM_FINGERS];

// ===============================
// 햅틱 설정
// ===============================

// haptic 값 0~1000
const int HAPTIC_MAX_VALUE = 1000;

const bool FLIP_FORCE_FEEDBACK = false;

// Unity에서 받은 햅틱 제한값
// 순서: 엄지, 검지, 중지, 약지, 소지
int hapticLimits[NUM_FINGERS] = {
  0, 0, 0, 0, 0
};

// ===============================
// 손가락 자동 보정
// ===============================

int minRaw[NUM_FINGERS] = {
  INT_MAX, INT_MAX, INT_MAX, INT_MAX, INT_MAX
};

int maxRaw[NUM_FINGERS] = {
  INT_MIN, INT_MIN, INT_MIN, INT_MIN, INT_MIN
};

// 손가락 방향 반전
// 손가락을 굽혔는데 c값이 0으로 가면 해당 손가락만 true
bool flipCurl[NUM_FINGERS] = {
  false,  // 엄지
  false,  // 검지
  false,  // 중지
  false,  // 약지
  false   // 소지
};

// true: raw/min/max/c/H 확인용 출력
// false: Unity 송신 규격 c0,c1,c2,c3,c4,qw,qx,qy,qz 출력
const bool DEBUG_MODE = false;

// ===============================
// MPU6050 변수
// ===============================

bool dmpReady = false;
uint16_t packetSize;
uint8_t fifoBuffer[64];

Quaternion q_current;
Quaternion q_base;

// ===============================
// 함수 선언
// ===============================

void runTareCalibration();
void handleSerialCommand();
void parseHapticCommand(String input);
void updateHapticServos();
void resetFingerCalibration();

void setup() {
  Serial.begin(115200);
  Serial.setTimeout(5);

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  // ===============================
  // 서보 초기화
  // ===============================

  ESP32PWM::allocateTimer(0);
  ESP32PWM::allocateTimer(1);
  ESP32PWM::allocateTimer(2);
  ESP32PWM::allocateTimer(3);

  for (int i = 0; i < NUM_FINGERS; i++) {
    hapticServos[i].setPeriodHertz(50);
    hapticServos[i].attach(servoPins[i], 500, 2400);

    // 초기 상태는 haptic 0 상태
    hapticServos[i].write(getServoAngleFromHaptic(0));
  }

  // ===============================
  // MPU6050 초기화
  // ===============================

  Wire.begin(21, 22);
  Wire.setClock(400000);

  mpu.initialize();

  if (!mpu.testConnection()) {
    while (1);
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

    delay(5000);
    runTareCalibration();
  } else {
    while (1);
  }
}

void loop() {
  if (!dmpReady) return;

  // Unity → ESP32 명령 처리
  // 예: H,0,800,0,0,0
  handleSerialCommand();

  // ===============================
  // 1. 가변저항 읽기 + min/max 계속 갱신
  // ===============================

  float c[NUM_FINGERS];
  int rawNow[NUM_FINGERS];

  for (int i = 0; i < NUM_FINGERS; i++) {
    int raw = readRawAveraged(potPins[i]);

    if (flipCurl[i]) {
      raw = ANALOG_MAX_VALUE - raw;
    }

    rawNow[i] = raw;

    updateFingerCalibration(i, raw);

    c[i] = readCurlFromRaw(raw, minRaw[i], maxRaw[i]);
  }

  // ===============================
  // 2. 서보 햅틱 적용
  // ===============================

  updateHapticServos();

  // ===============================
  // 3. MPU6050 Quaternion 읽기
  // ===============================

  if (mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
    mpu.dmpGetQuaternion(&q_current, fifoBuffer);

    // q_relative = q_base^-1 * q_current
    float q1_w = q_base.w;
    float q1_x = -q_base.x;
    float q1_y = -q_base.y;
    float q1_z = -q_base.z;

    float q2_w = q_current.w;
    float q2_x = q_current.x;
    float q2_y = q_current.y;
    float q2_z = q_current.z;

    float qw = q1_w * q2_w - q1_x * q2_x - q1_y * q2_y - q1_z * q2_z;
    float qx = q1_w * q2_x + q1_x * q2_w + q1_y * q2_z - q1_z * q2_y;
    float qy = q1_w * q2_y - q1_x * q2_z + q1_y * q2_w + q1_z * q2_x;
    float qz = q1_w * q2_z + q1_x * q2_y - q1_y * q2_x + q1_z * q2_w;

    if (DEBUG_MODE) {
      printDebug(rawNow, c, qw, qx, qy, qz);
    } else {
      printUnityCSV(c, qw, qx, qy, qz);
    }
  }

  delay(16);
}

// ===============================
// ESP32 → Unity 출력
// c0,c1,c2,c3,c4,qw,qx,qy,qz
// ===============================

void printUnityCSV(float c[NUM_FINGERS], float qw, float qx, float qy, float qz) {
  for (int i = 0; i < NUM_FINGERS; i++) {
    Serial.print(c[i], 4);
    Serial.print(",");
  }

  Serial.print(qw, 4); Serial.print(",");
  Serial.print(qx, 4); Serial.print(",");
  Serial.print(qy, 4); Serial.print(",");
  Serial.println(qz, 4);
}

// ===============================
// 디버그 출력
// ===============================

void printDebug(int rawNow[NUM_FINGERS], float c[NUM_FINGERS],
                float qw, float qx, float qy, float qz) {
  static unsigned long lastDebugPrint = 0;

  if (millis() - lastDebugPrint < 1000) {
    return;
  }

  lastDebugPrint = millis();

  Serial.println("--------------------------------------------------");

  for (int i = 0; i < NUM_FINGERS; i++) {
    Serial.print("F");
    Serial.print(i);

    if (i == 0) Serial.print(" Thumb ");
    if (i == 1) Serial.print(" Index ");
    if (i == 2) Serial.print(" Middle");
    if (i == 3) Serial.print(" Ring  ");
    if (i == 4) Serial.print(" Pinky ");

    Serial.print(" | raw=");
    Serial.print(rawNow[i]);

    Serial.print(" | min=");
    Serial.print(minRaw[i]);

    Serial.print(" | max=");
    Serial.print(maxRaw[i]);

    Serial.print(" | c=");
    Serial.print(c[i], 3);

    Serial.print(" | H=");
    Serial.print(hapticLimits[i]);

    Serial.print(" | angle=");
    Serial.println(getServoAngleFromHaptic(hapticLimits[i]));
  }

  Serial.print("Qrel ");
  Serial.print("w=");
  Serial.print(qw, 3);
  Serial.print(" x=");
  Serial.print(qx, 3);
  Serial.print(" y=");
  Serial.print(qy, 3);
  Serial.print(" z=");
  Serial.println(qz, 3);
}

// ===============================
// Unity → ESP32 명령 처리
// ===============================

void handleSerialCommand() {
  if (Serial.available() <= 0) return;

  String input = Serial.readStringUntil('\n');
  input.trim();

  if (input.length() == 0) return;

  // IMU 영점 재조절
  if (input == "t" || input == "T") {
    runTareCalibration();
    return;
  }

  // 손가락 min/max 초기화
  if (input == "r" || input == "R") {
    resetFingerCalibration();
    return;
  }

  // 햅틱 명령
  // 형식: H,thumb,index,middle,ring,pinky(예: H,0,800,0,0,0)
  if (input.charAt(0) == 'H' || input.charAt(0) == 'h') {
    parseHapticCommand(input);
    return;
  }
}

// ===============================
// 햅틱 명령 파싱
// H,thumb,index,middle,ring,pinky
// 값 범위: 0~1000
// ===============================

void parseHapticCommand(String input) {
  int firstComma = input.indexOf(',');

  if (firstComma == -1) {
    return;
  }

  String values = input.substring(firstComma + 1);

  for (int i = 0; i < NUM_FINGERS; i++) {
    int commaIndex = values.indexOf(',');
    String valueStr;

    if (commaIndex == -1) {
      valueStr = values;
      values = "";
    } else {
      valueStr = values.substring(0, commaIndex);
      values = values.substring(commaIndex + 1);
    }

    int limit = valueStr.toInt();

    if (limit < 0) limit = 0;
    if (limit > HAPTIC_MAX_VALUE) limit = HAPTIC_MAX_VALUE;

    hapticLimits[i] = limit;

    if (values.length() == 0) {
      break;
    }
  }
}

// ===============================
// 햅틱값 0~1000 → 서보각 0~180 변환
// ===============================

int getServoAngleFromHaptic(int hapticValue) {
  if (hapticValue < 0) hapticValue = 0;
  if (hapticValue > HAPTIC_MAX_VALUE) hapticValue = HAPTIC_MAX_VALUE;

  float angle;

  if (FLIP_FORCE_FEEDBACK) {
    angle = hapticValue / 1000.0f * 180.0f;
  } else {
    angle = 180.0f - hapticValue / 1000.0f * 180.0f;
  }

  if (angle < 0) angle = 0;
  if (angle > 180) angle = 180;

  return (int)angle;
}

// ===============================
// 서보 햅틱 적용
// ===============================

void updateHapticServos() {
  for (int i = 0; i < NUM_FINGERS; i++) {
    int angle = getServoAngleFromHaptic(hapticLimits[i]);
    hapticServos[i].write(angle);
  }
}

// ===============================
// IMU 영점 조절
// ===============================

void runTareCalibration() {
  mpu.resetFIFO();
  delay(50);

  unsigned long startTime = millis();

  float sumW = 0;
  float sumX = 0;
  float sumY = 0;
  float sumZ = 0;
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
    q_base.w = sumW / sampleCount;
    q_base.x = sumX / sampleCount;
    q_base.y = sumY / sampleCount;
    q_base.z = sumZ / sampleCount;

    float mag = sqrt(
      q_base.w * q_base.w +
      q_base.x * q_base.x +
      q_base.y * q_base.y +
      q_base.z * q_base.z
    );

    if (mag > 0) {
      q_base.w /= mag;
      q_base.x /= mag;
      q_base.y /= mag;
      q_base.z /= mag;
    } else {
      q_base.w = 1;
      q_base.x = 0;
      q_base.y = 0;
      q_base.z = 0;
    }
  } else {
    q_base.w = 1;
    q_base.x = 0;
    q_base.y = 0;
    q_base.z = 0;
  }

  mpu.resetFIFO();
}

// ===============================
// ADC 평균 읽기
// ===============================

int readRawAveraged(int pin) {
  long sum = 0;

  for (int i = 0; i < 3; i++) {
    sum += analogRead(pin);
    delayMicroseconds(200);
  }

  return sum / 3;
}

// ===============================
// 손가락 min/max 계속 갱신
// ===============================

void updateFingerCalibration(int fingerIndex, int raw) {
  if (raw < minRaw[fingerIndex]) {
    minRaw[fingerIndex] = raw;
  }

  if (raw > maxRaw[fingerIndex]) {
    maxRaw[fingerIndex] = raw;
  }
}

// ===============================
// raw → 0.0~1.0 정규화
// ===============================

float readCurlFromRaw(int raw, int minVal, int maxVal) {
  int range = maxVal - minVal;

  if (range < 30) {
    return 0.0;
  }

  float normalized = (float)(raw - minVal) / (float)range;

  if (normalized < 0.0) normalized = 0.0;
  if (normalized > 1.0) normalized = 1.0;

  return normalized;
}

// ===============================
// 손가락 캘리브레이션 초기화
// ===============================

void resetFingerCalibration() {
  for (int i = 0; i < NUM_FINGERS; i++) {
    minRaw[i] = INT_MAX;
    maxRaw[i] = INT_MIN;
  }
}
