FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY ./output/TimeToDo .

EXPOSE 80
EXPOSE 443
ENTRYPOINT ["dotnet", "TimeToDo.Api.dll"]
