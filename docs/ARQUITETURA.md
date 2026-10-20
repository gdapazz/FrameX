# Arquitetura

## Visão geral

```
Framex.slnx
├── src/Framex.Core            Domínio e regras (sem dependência de Windows/WPF)
├── src/Framex.Infrastructure  Integrações reais: sensores, PresentMon, processos, SQLite, registro
├── src/Framex.App             Aplicativo WPF (MVVM), overlay, bandeja, atalhos
├── tests/Framex.Tests         Testes unitários e de integração (xUnit)
└── tools/Framex.UiSmoke       Verificação offscreen de todas as telas (dados sintéticos rotulados)
```

As dependências seguem uma direção: `App → Infrastructure → Core`. O Core define contratos (`ISensorProvider`, `IFrameCaptureProvider`, `IProcessSnapshotProvider`, `ISessionRepository`…) e a Infrastructure os implementa. Os provedores simulados ficam somente em `tests/Framex.Tests/TestSupport.cs`.

## Core (`Framex.Core`)

| Pasta | Responsabilidade |
|---|---|
| `Domain` | Tipos de métricas, sensores (com `SensorAvailability` e motivo), jogos e sessões. |
| `Monitoring` | `HardwareMonitoringService` agrega provedores em `HardwareSnapshot`; `SensorValidator` rejeita leituras fora de faixa física; `RingBuffer`; `UnitFormatter` (°C/°F, GiB/GB). |
| `FrameCapture` | Contratos de captura, `FrameStatistics` (FPS médio harmônico, lows, percentis) e `LiveFrameAggregator` (janelas deslizantes para o painel e o overlay). |
| `GameDetection` | `GameDetectionEngine` (lógica pura sobre instantâneos de processos) e `GameDetectionService` (laço periódico, eventos de início/fim, pendências de confirmação). |
| `SessionTracking` | `SessionRecorder`: cria sessão ao detectar jogo, acumula amostras em buffers, grava em lotes transacionais e finaliza com o resumo; recupera sessões interrompidas como “incompletas”. |
| `Reporting` | `SessionStatisticsCalculator` (resumo, lacunas, quedas), `SessionComparer` (diferenças e avisos de condições), `ReportExporter` (CSV/JSON/HTML offline, sem caminhos pessoais). |
| `Alerts` | `AlertEngine` (duração mínima, cooldown, escopo por jogo) e `AlertService`. |
| `Customization` | `AppSettings`, perfis (`PerformanceTuning`), `ThemeDefinition` (8 temas embutidos), `OverlayLayout` (presets), `ConfigPackage` (importação/exportação versionada e validada). |
| `Persistence` | Interfaces dos repositórios. |

## Infrastructure (`Framex.Infrastructure`)

| Componente | Detalhes |
|---|---|
| `LibreHardwareSensorProvider` | LibreHardwareMonitorLib (MPL-2.0). Mapeia sensores para métricas e marca como indisponível o que o driver não consegue ler (ex.: consumo de CPU = 0 W sem acesso a MSR). |
| `WindowsSystemProvider` | Memória física e comprometida (`GetPerformanceInfo`, sem privilégios) e inventário estático (WMI, `EnumDisplayDevices`, `DriveInfo`, `GetSystemPowerStatus`). |
| `PresentMonCaptureProvider` | Executa o PresentMon fornecido pelo usuário como processo filho dentro de um *Job Object* (`ChildProcessJob`, *kill on close*), lê o CSV da saída padrão (`PresentMonCsvParser`), entrega lotes a cada 250 ms e encerra a sessão ETW ao parar. |
| `Win32ProcessSnapshotProvider` | Instantâneo de processos (nome, caminho, janela visível, memória). |
| `SqliteDatabase` / `SqliteRepositories` | SQLite em WAL, migrações por `PRAGMA user_version` com backup prévio, verificação de integridade (banco corrompido é isolado e recriado). |
| `StartupRegistration` | Valor `FRAMEX` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (visível no Gerenciador de Tarefas), com `--minimized`. |
| `AppPaths` | `%LOCALAPPDATA%\FRAMEX` (banco, logs, tools); exportações em `Documentos\FRAMEX\Relatórios`. |

### Banco de dados

Tabelas: `games`, `game_executables`, `ignored_processes`, `sessions`, `metric_samples`, `frame_chunks` (frametimes em blocos binários de `float32`, um bloco por lote gravado), `session_events`, `settings` (JSON), `alert_history`. Índices por data, jogo e estado de sessão.

## App (`Framex.App`)

- **`AppHost`**: composição (Microsoft.Extensions.DependencyInjection), inicialização de dados, aplicação de configurações em tempo de execução e encerramento ordenado (`DisposeAsync`).
- **`App.xaml.cs`**: instância única (mutex + evento de ativação), tratadores globais de exceção, bandeja, atalhos, roteamento de alertas e eventos de sessão.
- **Serviços de UI**: `LiveDataService` (ritmo de atualização pelo perfil), `OverlayService` (janela do HUD), `TrayService`, `HotkeyService` (`RegisterHotKey`), `ThemeService` (tokens dinâmicos), `ExportService`, `DialogService`, `Navigator`.
- **MVVM**: CommunityToolkit.Mvvm; uma ViewModel por página; views XAML sem lógica de negócio.
- **Controles próprios** desenhados com `OnRender`: `ArcGauge`, `LiveChart`, `TimeSeriesChart` (lacunas como NaN, zoom/seleção/hover sincronizados por propriedades de dependência), `HistogramChart`, `DashboardPanel` (grade responsiva), `ColorPicker`, `HotkeyBox`.
- **Design tokens** em `Themes/Tokens.xaml` (`B.*` pincéis, `C.*` cores, `R.*` raios, `T.*` tamanhos, `S.*` espaçamentos, `F.*` fontes, `O.*` opacidades), substituídos pelo `ThemeService` ao trocar de tema.

## Fluxo de uma sessão

1. `GameDetectionService` encontra um jogo conhecido (ou pede confirmação de um candidato).
2. `SessionRecorder` cria a sessão, registra as condições (resolução, perfil, hardware) e inicia o PresentMon para o PID.
3. Amostras de hardware (pelo intervalo do perfil) e frametimes são gravadas em lotes; ausências viram lacunas.
4. Ao fechar o jogo, a sessão é finalizada, o resumo é calculado e o app notifica pela bandeja.
5. Se o FRAMEX for encerrado à força, a sessão fica “ativa” no banco e é marcada como incompleta na próxima abertura, preservando o que já foi gravado.

## Encerramento

`ExitAsync` esconde a janela, para o laço de dados, descarta bandeja/overlay/atalhos no thread de UI e depois aguarda o encerramento assíncrono (gravação final da sessão, parada do PresentMon). O Job Object garante que nenhum processo auxiliar sobreviva ao FRAMEX.
