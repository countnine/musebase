# 가사 서버 컨테이너 이미지.
#
# 빌드 컨텍스트는 **저장소 루트**다(Musebase.Core를 함께 복사해야 하므로):
#   docker build -f src/Musebase.Server/Dockerfile -t musebase-server .
#
# 실행에 필요한 것은 DB 볼륨(/data)과 토큰뿐이다 — deploy/docker-compose.yml 참고.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 프로젝트 파일만 먼저 복사해 복원 결과를 레이어 캐시에 남긴다(코드만 바뀌면 restore를 건너뛴다).
COPY src/Musebase.Core/Musebase.Core.csproj src/Musebase.Core/
COPY src/Musebase.Server/Musebase.Server.csproj src/Musebase.Server/
RUN dotnet restore src/Musebase.Server/Musebase.Server.csproj

COPY src/Musebase.Core/ src/Musebase.Core/
COPY src/Musebase.Server/ src/Musebase.Server/
RUN dotnet publish src/Musebase.Server/Musebase.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# tzdata: 관리자 화면이 KST로 표시하려면 필요(없으면 UTC로 강등된다).
# sqlite3: 컨테이너 안에서 백업(.backup)·점검(integrity_check)을 돌리기 위해.
# curl: HEALTHCHECK용(aspnet 이미지에는 들어 있지 않다).
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata sqlite3 curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# DB는 반드시 볼륨에 둔다 — 이미지를 새로 올려도 가사가 남는다.
ENV MUSEBASE_DB=/data/lyrics.db \
    ASPNETCORE_URLS=http://0.0.0.0:5180 \
    MUSEBASE_TZ=Asia/Seoul

# 이름 있는 볼륨은 이미지의 해당 디렉터리를 소유권째 복사해 초기화된다 —
# VOLUME 선언 **전에** 앱 사용자(비루트, $APP_UID=1654) 소유로 만들어 둬야
# 컨테이너가 DB를 열 수 있다(안 하면 'unable to open database file'로 죽는다).
RUN mkdir -p /data /backups && chown -R $APP_UID:$APP_UID /data /backups
VOLUME ["/data"]
EXPOSE 5180

# 컨테이너 오케스트레이터가 살아있음을 판단할 수 있게(무인증 엔드포인트).
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
  CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:5180/v1/healthz || exit 1"]

# 컨테이너는 루트로 돌 이유가 없다. /data 소유권은 compose의 볼륨 초기화에 맡긴다.
USER $APP_UID
ENTRYPOINT ["dotnet", "Musebase.Server.dll"]
