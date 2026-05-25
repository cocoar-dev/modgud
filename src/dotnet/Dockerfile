FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY ./output/Modgud .

EXPOSE 80
EXPOSE 443
ENTRYPOINT ["dotnet", "Modgud.Api.dll"]
