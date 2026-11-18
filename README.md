# 셔틀 랙 강화학습 (Unity ML-Agents + Blender)

물류센터 파렛트 셔틀 랙에서 4방향 셔틀이 강화학습으로 입출고 운반을 배우는 프로젝트입니다.
대상 환경: Unity 6000.6.8f1, Python ml-agents 1.1.0, PyTorch 2.8.0 (CPU)

## 폴더 구성

| 경로 | 내용 |
|---|---|
| `blender/build_assets.py` | 3D 모듈 생성 스크립트 (FBX 12종 + `dimensions.json`) |
| `blender/preview_scene.py` | 샘플 랙 조립 후 미리보기 렌더 |
| `unity/` | Unity 프로젝트 폴더 (Unity Hub에서 이 폴더를 엽니다) |
| `unity/Assets/ShuttleRL/Models/` | 생성된 FBX (바로 사용 가능) |
| `unity/Assets/ShuttleRL/Scripts/Core/` | 시뮬레이션 코어 (Unity 비의존 C#): 랙 레이아웃, 셔틀·리프트 동작, 컨트롤러(WCS), 관측·보상, 규칙 기반 셔틀 |
| `unity/Assets/ShuttleRL/Scripts/Runtime/` | Unity 연결: `RackEnvironment`, `ShuttleAgent`, 3D 표시 |
| `unity/Assets/ShuttleRL/Scripts/Editor/` | 메뉴로 씬 자동 생성 |
| `config/shuttle_poca.yaml` | MA-POCA 학습 설정 + 커리큘럼 |
| `tools/CoreSim/` | Unity 없이 코어를 돌려 보는 검증 프로그램 (.NET 8) |
| `docs/lift_shuttle_design.md` | 리프트·셔틀 구조와 기능 정의 |

## 1. 받기와 업데이트 (git)
처음 한 번:
```
git clone https://github.com/parksehoon1971/shuttle-rl.git
```
1. Unity Hub → **Add project from disk** → 받은 폴더 안의 `unity` 폴더를 선택해 Unity 6000.6.8f1로 엽니다.
2. ML-Agents는 `unity/Packages/manifest.json`에 들어 있어서 프로젝트를 열면 자동으로 설치됩니다 (agv-rl-center와 같은 `release/4.0.3`, GitHub에서 받으므로 PC에 git이 있어야 합니다).
3. 메뉴 **ShuttleRL → Create Demo Scene (1 rack, 3D)** 을 누르고 Play 합니다.
   처음에는 규칙 기반 셔틀(Heuristic Only)로 움직입니다. 3층, 레인 6열, 레인당 6셀, 층당 셔틀 2대입니다.
   `ShuttleRack` 오브젝트의 `Config`에서 층수, 레인 수, 셀 수, 셔틀 수, 입출고량을 바꿀 수 있습니다. 리프트 수는 자동 계산됩니다.

이후 수정본 받기:
```
git pull
```
Unity를 켜 둔 상태여도 됩니다. 창으로 돌아오면 스크립트와 모델이 다시 불러와집니다. `Library/`, `Temp/`, `results/` 같은 생성 폴더는 `.gitignore`로 제외되어 있어서 pull 할 때 충돌하지 않습니다.

기존 Unity 프로젝트에서 쓰고 싶으면 `unity/Assets/ShuttleRL` 폴더를 그 프로젝트의 `Assets`에 복사해도 되지만, 그 경우 업데이트는 다시 복사해야 합니다.

**렌더 파이프라인(URP):** 프로젝트를 처음 열면 `ShuttleRLUrpSetup` 스크립트가 자동으로 (1) 이 에디터 버전에 맞는 URP 패키지를 설치하고, (2) `Assets/ShuttleRL/Settings`에 URP 에셋을 만들어 Graphics/Quality 설정에 지정하고, (3) 모델을 다시 불러와 재질을 URP Lit으로 바꿉니다. 설치 후 Unity가 한 번 다시 컴파일하는 동안 기다리면 됩니다. 자동으로 안 됐으면 메뉴 **ShuttleRL → Set Up URP**를 누르세요.
URP 설치로 `unity/Packages/manifest.json`에 URP 한 줄이 PC에서만 추가되는데, 이후 `git pull`에는 지장이 없습니다.

Input Manager 경고는 이 프로젝트가 키보드 입력을 쓰지 않아서 영향이 없습니다.

## 2. 학습
1. 새 씬에서 **ShuttleRL → Create Training Scene (8 racks, headless)** 를 실행하고 씬을 저장합니다.
2. 터미널에서:
   ```
   cd shuttle-rl
   mlagents-learn config/shuttle_poca.yaml --run-id=shuttle_v1
   ```
   메시지가 나오면 Unity에서 Play. 더 빠르게 하려면 씬을 빌드한 뒤 `--env=<빌드 경로> --no-graphics --num-envs=4`.
3. 진행 확인: `tensorboard --logdir results`. `Rack/Throughput per hour`, `Rack/Deadlock`, `Rack/Inbound cycle s` 등을 기록합니다.
4. 학습된 `results/shuttle_v1/Shuttle.onnx`를 데모 씬의 `ShuttleAgentTemplate` → Behavior Parameters → Model 에 넣고 Behavior Type을 Inference Only로 바꾸면 학습된 셔틀이 움직입니다.

## 3. 강화학습 설계 요약
- **에이전트:** 셔틀 1대 = 에이전트 1개, 랙 하나의 셔틀 전체가 한 그룹(MA-POCA). 셔틀 수가 에피소드마다 바뀌어도 됩니다.
- **결정 시점:** 이동 중에는 다음 셀에서 멈추기 위해 감속을 시작해야 하는 지점에서, 정지 중에는 잭 동작이나 대기가 끝날 때 결정합니다. 같은 방향을 고르면 멈추지 않고 가속, 정속, 감속으로 연속 주행하고, 다른 행동을 고르면 그 셀에 정지한 뒤 실행합니다. 지나간 셀은 셔틀 중심이 셀 중간을 넘으면 비워집니다. 시뮬레이션은 이벤트 기반이라 프레임 속도와 무관합니다.
- **행동(6):** 대기, +X, -X, +Y, -Y, 잭(들기/내리기). 불가능한 행동은 마스킹됩니다 (레일 없음, 다른 셔틀이 있는 셀, 짐 실은 채로 파렛트 아래 진입 등).
- **관측(309):** 자기 상태 15개 (적재 여부, 작업 단계, 바퀴 방향, 목표까지 상대 위치·거리, 최단경로 방향 힌트, 현재 속도) + 주변 7×7 셀 × 6채널 (레일, 통과 가능, 다른 셔틀, 그 셔틀의 작업 상태, 파렛트, 목표 셀).
- **보상:** 개인: 파렛트 들기 +0.1, 내리기 +0.5, 목표 쪽 진행 +0.01/셀, 작업 중 시간 -0.0005/초, 잘못된 행동 -0.05. 그룹: 작업 완료마다 +0.5, 교착(300초간 진척 없음) -1 후 에피소드 종료.
- **컨트롤러(WCS, 규칙):** 입출고 지시 생성, 입고 셀 결정, 가까운 빈 셔틀에 작업 배정, 리프트 운전.

## 4. 규칙 기반 셔틀 기준값 (tools/CoreSim, 시뮬레이션 1시간)
| 랙 | 셔틀 | 입출고 요청 | 처리량 | 교착 |
|---|---|---|---|---|
| 2층, 레인 3열×4셀 | 층당 2대 | 40/40 per h | 42~61/h | 3회 중 3회 |
| 4층, 레인 6열×6셀 | 층당 3대 | 120/120 per h | 148~241/h | 3회 중 1회 |
| 6층, 통로 2개, 레인 10열×10셀 | 층당 4대 | 300/300 per h | 521~600/h | 0회 |

좁은 랙일수록 셔틀끼리 레인 입구와 통로에서 마주쳐 교착이 생깁니다. 강화학습이 줄여야 할 부분입니다.
실행: `cd tools/CoreSim && dotnet run -c Release -- 3`

## 5. 현재 가정과 다음 확장
- 셔틀은 자기 층에서만 이동합니다. 리프트 캐리지와 P&D가 같은 롤러 레일이라 셔틀의 리프트 탑승은 구조 변경 없이 추가할 수 있습니다 (`docs/lift_shuttle_design.md`).
- 레인은 깊은 LIFO 구조라서 앞쪽 파렛트만 꺼낼 수 있고, 레인당 진행 중인 작업은 1개로 제한합니다.
- 메인 통로가 여러 개면 통로끼리 연결되어 있지 않다고 보고 셔틀을 통로별로 나눠 배치합니다. 연결 통로가 있다면 알려주세요.
- 치수는 일반값입니다 (파렛트 1.1 m, 층고 1.6 m, 레인 셀 1.3 m, 레인 간격 1.4 m, 메인 통로 폭 1.4 m로 파렛트가 기둥에 닿지 않음). `build_assets.py`와 `RackConfig`에서 바꿀 수 있습니다.
