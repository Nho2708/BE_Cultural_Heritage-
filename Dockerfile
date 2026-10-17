# syntax=docker/dockerfile:1
# Ảnh chạy backend trên máy chủ Linux (Google Cloud), dựng bằng: docker build -t dsvhvn-api .
# Ảnh kèm /app/migrations.sql (idempotent): script triển khai trên máy chủ áp tệp này trước khi chạy bản mới.
# Cấu hình bí mật không nằm trong ảnh: máy chủ gắn appsettings.Production.json vào /app lúc chạy.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dung
WORKDIR /src
ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

# Khôi phục gói trước khi chép mã nguồn để Docker giữ lớp này giữa các lần dựng.
COPY global.json Directory.Build.props ./
COPY .config/dotnet-tools.json .config/
COPY src/DSVHVN.Domain/DSVHVN.Domain.csproj src/DSVHVN.Domain/
COPY src/DSVHVN.Application/DSVHVN.Application.csproj src/DSVHVN.Application/
COPY src/DSVHVN.Infrastructure/DSVHVN.Infrastructure.csproj src/DSVHVN.Infrastructure/
COPY src/DSVHVN.Api/DSVHVN.Api.csproj src/DSVHVN.Api/
RUN dotnet restore src/DSVHVN.Api/DSVHVN.Api.csproj && dotnet tool restore

COPY src/ src/
RUN dotnet publish src/DSVHVN.Api -c Release --no-restore -o /out \
 && dotnet tool run dotnet-ef migrations script --idempotent \
      --project src/DSVHVN.Infrastructure --startup-project src/DSVHVN.Api \
      --configuration Release --no-build -o /out/migrations.sql

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=dung /out .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true
EXPOSE 8080
# Người dùng không phải root có sẵn trong ảnh ASP.NET (mã 1654): tệp cấu hình và kho tệp trên máy chủ cấp quyền cho mã này.
USER $APP_UID
ENTRYPOINT ["dotnet", "DSVHVN.Api.dll"]
