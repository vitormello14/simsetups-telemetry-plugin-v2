# SimSetups Telemetry Plugin v2.5.0

Plugin SimHub que captura telemetria do **F1 25** e envia para o app Sim Setups.

Sucessor da v2.4.x (do Lovable). Esta versão corrige 3 bugs reportados em produção
sem mexer no que já funcionava.

---

## Bugs corrigidos nesta versão

### 1. Última volta não era gravada
**Causa:** o gatilho de envio era `CompletedLaps` aumentar entre dois `DataUpdate`.
Quando o jogador encerrava a sessão logo após cruzar a linha, o `End()` do plugin era
chamado antes do próximo `DataUpdate`, e a volta ficava em `LastLapTime` sem nunca
disparar o envio.

**Correção (`FIX_FINAL_LAP_FLUSH`):** capturamos uma snapshot do estado da volta a cada
`DataUpdate`. No `End()` (e na detecção de troca de pista/sessão), se há volta pendente,
fazemos um flush usando essa snapshot. Flag `_finalLapFlushed` impede envio duplicado.

### 2 e 3. Voltas de outros pilotos atribuídas ao usuário em lobbies online
**Causa:** quando o jogador está nos boxes ou assistindo outro piloto, o `data.NewData`
do SimHub reflete o **carro em foco da câmera**, não necessariamente o carro do jogador.
Em short qualy/lobby online, isso fazia voltas mais rápidas de outros pilotos serem
gravadas como sendo do usuário.

A v2.4.1/2.4.2 do Lovable tentou resolver isso de duas formas que não funcionaram:
- Heurística de input (throttle/speed) — falha quando o jogador está parado nos boxes
- "Conserto" de `lap_time` usando o tempo do `Opponent.IsPlayer=true` — mantinha
  top_speed/wear/sectors do carro espectado, criando voltas Frankenstein

**Correção em 3 camadas:**

- **`FIX_STRICT_OPPONENT`:** em sessão online, exigimos que `Opponent.IsPlayer=true`
  exista. Se não, rejeita. Removido o "Frankenstein" — se há divergência > 0.5s entre
  `Opponent.LastLapTime` e `data.NewData.LastLapTime`, rejeita a volta inteira.
- **`FIX_LAP_JUMP_GUARD`:** se `CompletedLaps` pula mais de 1 entre updates, é sinal
  forte de troca de carro foco. Ressincronizamos sem enviar.
- **`FIX_SETTLE_WINDOW`:** após detectar volta nova, esperamos 200ms antes de enviar
  para que `LastLapTime` estabilize e o `Opponent` confirme o valor.

---

## Arquitetura do projeto

```
.
├── src/
│   └── TelemetryPlugin.cs          ← código C# do plugin
├── lib/
│   ├── SimHub_Plugins.dll          ← DLL real do SimHub (referência de build)
│   └── GameReaderCommon.dll        ← DLL real do SimHub (referência de build)
├── .github/
│   └── workflows/
│       └── build.yml               ← GitHub Actions: build automático
├── SimSetups.TelemetryPlugin.csproj
├── .gitignore
└── README.md
```

**Por que as DLLs em `lib/`?** A v2.4.1/2.4.2 do Lovable usava `Stub.cs` com classes
mock do SimHub. Compilava, mas em runtime dava type mismatch silencioso e o
`DataUpdate` crashava. Compilando contra as DLLs reais (mesmas que o SimHub carrega
em runtime), garantimos que as assinaturas batem.

---

## Como buildar

### Via GitHub Actions (recomendado)
Toda vez que o repo recebe push em `main`, o workflow builda automaticamente e
disponibiliza o artefato `SimSetups_TelemetryPlugin-v2.5.0` na aba **Actions**.

Você baixa o `.zip`, extrai a DLL, joga na pasta do SimHub.

### Localmente (Windows com .NET 8 SDK)
```bash
dotnet restore
dotnet build --configuration Release -p:Version=2.5.0
```
A DLL final fica em `bin/Release/SimSetups.TelemetryPlugin.dll` — copie e renomeie
para `SimSetups_TelemetryPlugin.dll` (com underscore) ao instalar no SimHub.

---

## Como instalar no SimHub

1. Feche o SimHub
2. Substitua o arquivo `SimSetups_TelemetryPlugin.dll` na pasta de instalação do SimHub
   (geralmente `C:\Program Files (x86)\SimHub\` ou `C:\Arquivos de Programas (x86)\SimHub\`)
3. Garanta que `SimSetups_Token.txt` está na mesma pasta com o token do app
4. Abra o SimHub
5. Em **Plugins**, "Sim Setups Telemetry" deve aparecer ativo

---

## Feature flags (debug)

No topo de `src/TelemetryPlugin.cs` há 4 flags `const bool` que controlam cada fix:

```csharp
private const bool FIX_FINAL_LAP_FLUSH      = true;
private const bool FIX_STRICT_OPPONENT      = true;
private const bool FIX_LAP_JUMP_GUARD       = true;
private const bool FIX_SETTLE_WINDOW        = true;
```

Se algum fix der falso-positivo em algum cenário, basta setar pra `false`,
recompilar e testar. Os 4 são independentes entre si.

---

## Versionamento

| Versão | Origem | Status |
|---|---|---|
| 2.4.0 | Lovable | Estável em produção. Bugs: última volta + spectator |
| 2.4.1 | Lovable | Tentativa anti-spectator com heurística — não funcionou |
| 2.4.2 | Lovable | Tentou consertar 2.4.1 — também não funcionou |
| **2.5.0** | **Este repo** | **Atual.** 4 fixes aditivos, build com DLLs reais |

A 2.4.1 e 2.4.2 não conseguiam nem gravar volta nenhuma em runtime (type mismatch
contra os Stubs). A 2.5.0 mantém 100% do que funcionava na 2.4.0 + adiciona os fixes.
