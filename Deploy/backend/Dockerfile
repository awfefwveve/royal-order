# 황실 칼전 백엔드 (기본 .NET 런타임만 필요)
# 빌드 전 (PostgreSQL 사용): cd Backend/src && dotnet publish -c Release -p:UsePostgres=true -o ../server
# 빌드:   docker build -f Deploy/backend/Dockerfile -t knifewar-backend .   (프로젝트 루트에서)
# 실행:   docker run -p 8080:8080 -e KW_TOKEN_SECRET=... -e KW_SERVER_SECRET=... -e KW_DB="Host=db;Database=knifewar;Username=kw;Password=..." -e KW_DEV_AUTH=0 knifewar-backend
FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app
COPY Backend/server/ /app/
ENV KW_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/KnifeWar.Backend.dll"]
