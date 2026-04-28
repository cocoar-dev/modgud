FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY ./output/Cocoar.Auth .

EXPOSE 80
EXPOSE 443
ENTRYPOINT ["dotnet", "Cocoar.Auth.Api.dll"]
