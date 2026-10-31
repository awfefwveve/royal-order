# 배포 구조 (북미·유럽·아시아 동시 출시)

```
                 [클라이언트]
                      │ HTTPS (로그인·매칭·전적)
                      ▼
   [백엔드 1곳]  api.astrelgames.com  ← Caddy/nginx (HTTPS) → knifewar-backend 컨테이너
        │        PostgreSQL (계정·전적, 관리형 DB 권장)
        │ 매칭 성사 → 빈 서버에 배정
        ▼
   [리전별 데디 서버]  knifewar-gameserver 컨테이너 (UDP 7777)
     kr · jp · sg · use · usw · euw · euc   ← 오케스트레이터(Edgegap 등)가 수요에 맞춰 띄움
```

## 1. 백엔드
1. PostgreSQL 준비 (관리형: AWS RDS, Supabase, Neon 등)
2. 패키지를 받을 수 있는 PC에서: `cd Backend/src && dotnet publish -c Release -p:UsePostgres=true -o ../server`
3. `docker build -f Deploy/backend/Dockerfile -t knifewar-backend .`
4. 실행 환경 변수
   - `KW_TOKEN_SECRET`, `KW_SERVER_SECRET` — 각각 32자 이상 무작위 값 (설정 파일에 두지 않기)
   - `KW_DB` — PostgreSQL 연결 문자열
   - `KW_DEV_AUTH=0` — 개발용 로그인 끄기 (자체 계정 로그인만 허용)
   - `KW_ADMIN_KEY` — 운영 API 키 (신고 목록 `/admin/reports`, 제재 `/admin/ban`, 해제 `/admin/unban`). 비우면 운영 API 꺼짐
   - `KW_SMTP_HOST`, `KW_SMTP_USER`, `KW_SMTP_PASSWORD`, `KW_SMTP_FROM` — 인증·비밀번호 재설정 메일 (AWS SES, SendGrid 등). 비우면 백엔드 콘솔에 코드 출력
5. 앞단에 HTTPS 리버스 프록시 (Caddy 한 줄: `api.astrelgames.com { reverse_proxy localhost:8080 }`)
6. `appsettings.json`의 `Regions[].PingUrl`을 리전별 핑 측정 주소로 (각 리전에 작은 `/ping` 응답만 두면 됨)

## 2. 데디 서버
1. 유니티: `KnifeWar > 멀티 > 리눅스 데디 서버 빌드` (Unity Hub에서 Linux Dedicated Server 모듈 필요)
2. `docker build -f Deploy/gameserver/Dockerfile -t knifewar-gameserver .`
3. 오케스트레이터에 이미지 등록, 포트 7777/UDP, 환경 변수 `KW_BACKEND`, `KW_SECRET`(= 백엔드 ServerSecret), `KW_REGION`
4. 서버는 켜지면 백엔드에 등록 → 경기 배정 → 끝나면 결과 보고 → 다음 경기 대기를 반복

## 3. 자체 계정 (라이엇·넥슨식)
- 이메일 + 비밀번호 가입, 이름#태그, 30일 자동 로그인 (PC에는 비밀번호가 아니라 세션 토큰만 저장)
- 비밀번호는 PBKDF2-SHA256(21만 회)으로만 저장, 로그인 10번 틀리면 15분 잠금, 비밀번호 바꾸면 다른 기기 로그인 전부 끊김
- 이메일 인증 강제: `RequireEmailVerification=true` (인증 전 매칭 불가)
- 출시 전 할 일: HTTPS 필수(리버스 프록시), 메일 발송 계정, 이용약관·개인정보처리방침 페이지
- 스팀 로그인 코드는 남아 있음 (`Steam.Enabled=false`로 꺼둠) — 나중에 스팀에도 올리면 켜기만 하면 됨

## 비용 감각 (설계 문서 기준)
- 백엔드: 작은 VM 1대 + 관리형 DB → 월 수만 원대에서 시작
- 데디 서버: 12명 한 판 시간당 약 0.15~0.25달러, 유저 많은 리전은 베어메탈 추가로 절감

## 4. 자체 런처 = 게임 자동 업데이트
1. 유니티 `KnifeWar > 배포 > 출시 빌드 + 패치 만들기` → 버전이 하나 오르고 `Builds/Release/{버전}/game`(설치본), `Builds/Patch/{버전}`(패치) 생성
2. `Builds/Patch/{버전}` 폴더를 CDN(Cloudflare R2, AWS S3+CloudFront 등)의 `{PatchBaseUrl}/{버전}/`에 업로드
3. 백엔드 최신 버전 변경 (재시작 없이): `POST /admin/client {"version":"0.1.4"}` (헤더 X-Admin-Key) — 또는 `KW_CLIENT_VERSION`, `KW_PATCH_URL`
4. 플레이어가 게임을 켜면 바뀐 파일만 받아서 스스로 재시작
- 처음 설치 파일: `Deploy/installer/KnifeWar.iss`를 Inno Setup(무료)으로 컴파일 → `KnifeWarSetup-버전.exe`를 홈페이지에 올림. 관리자 권한 없이 `%LocalAppData%\Programs`에 설치되어야 자동 업데이트가 파일을 덮어쓸 수 있음
- 점검: `POST /admin/client {"maintenance":true,"notice":"23:00~24:00 점검"}` → 로그인·매칭이 막히고 메뉴에 공지
- 개발 PC에서 패치 시험: 백엔드 설정 `Client.PatchDir`에 `Builds/Patch` 경로, `PatchBaseUrl`에 `http://127.0.0.1:8080/patch` → 백엔드가 패치 파일을 직접 내려줌

## 5. 테스트 서버 띄우기 (Vultr)

1. 서버 묶음 올리기: `Builds/royalorder-server.tar.gz` → R2 `patch` 버킷 `server/` 폴더.
2. Vultr에서 서버 만들기: 서울, Ubuntu 24.04, 시작 스크립트 `royalorder` 선택 (`Deploy/vultr/startup.sh`).
3. Cloudflare DNS: `api` A 레코드 → 서버 IP, **프록시 끔(회색 구름)**. 몇 분 뒤 HTTPS 인증서가 자동으로 붙는다.
4. 확인: `https://api.astrelgames.com/ping` 이 열리면 준비 끝. 설치 기록은 서버의 `/var/log/royalorder-setup.log`.
5. 테스트 끝나면 서버 삭제 (데이터도 같이 사라짐).

서버가 알아서 하는 것:
- HTTPS (Caddy). 백엔드 8080은 서버 안에서만 열림 → 비밀번호가 암호화 없이 오가지 않음
- DB 매일 새벽 4시 백업, 14일 보관 (`/var/backups/royalorder`). 출시 때는 서버 밖(R2 등)에도 복사해야 함
- 1분마다 백엔드 상태 확인, 3번 연속 응답 없으면 재시작 (`/var/log/royalorder-health.log`)
- 게임은 출시 빌드면 `https://api.astrelgames.com`, 에디터면 `http://127.0.0.1:8080` 에 붙는다
