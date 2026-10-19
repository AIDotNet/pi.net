FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src
COPY . .
RUN dotnet publish src/Pi.Evals/Pi.Evals.csproj --configuration Release --output /out -m:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS evaluator
WORKDIR /app
COPY --from=publish /out/ ./
RUN chmod 0400 /app/Pi.Evals.dll \
    && mkdir -p /run/pi-eval-secrets /artifacts \
    && touch /run/pi-eval-secrets/auth.json \
    && chmod 0700 /run/pi-eval-secrets
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PI_EVAL_CONTAINER=1
ENTRYPOINT ["dotnet", "Pi.Evals.dll", "container-task"]

FROM evaluator AS without_docs
RUN rm -rf /app/docs /app/examples /app/README.md /app/CHANGELOG.md
ENV PI_EVAL_VARIANT=without_docs

FROM evaluator AS with_docs
ENV PI_EVAL_VARIANT=with_docs
