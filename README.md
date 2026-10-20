# FRAMEX

Monitoramento e análise de desempenho em jogos para Windows 10/11 (x64).

O FRAMEX mostra leituras reais de CPU, GPU, RAM e VRAM, mede FPS e frametime com o PresentMon (eventos ETW do Windows, sem injeção de código nos jogos), detecta jogos automaticamente, grava sessões em um banco SQLite local e gera relatórios com gráficos, estatísticas e comparação entre sessões. Um overlay (HUD) independente exibe as métricas sobre o jogo.

Feito por: Gdapazz

## Princípios

- **Somente dados reais.** Um sensor indisponível aparece como “—” com o motivo; nunca como zero ou estimativa. Dados simulados existem apenas nos testes e na ferramenta de verificação de UI, sempre rotulados.
- **Sem injeção.** Os quadros vêm do PresentMon, que lê eventos ETW. O FRAMEX não carrega DLLs em jogos e não interage com sistemas antitrapaça.
- **Local e privado.** Não há conta, telemetria ou envio automático. Os dados ficam em `%LOCALAPPDATA%\FRAMEX`. Relatórios exportados não contêm caminhos nem nome de usuário.
- **Sem promessas de causa.** Os relatórios mostram correlação temporal entre eventos e métricas, não a causa.

## Recursos

| Área | O que faz |
|---|---|
| Painel | Medidores de CPU/GPU/RAM/VRAM, quadros ao vivo, histórico de 2 min, uso por núcleo, resumo do sistema, sessões recentes. Layout editável. |
| Jogos | Biblioteca, detecção automática (lojas conhecidas + confirmação do usuário), associação manual de processos, lista de ignorados. |
| Hardware | Componentes, provedores de sensores com estado e motivo, lista completa de sensores com filtro. |
| Nota do Setup | Identifica CPU, GPU, RAM e armazenamento localmente e calcula uma nota de 0 a 1000. CPU e GPU usam medianas públicas do Blender Open Data (CC0); RAM e armazenamento usam uma estimativa pela capacidade. Mostra problemas de identificação, o cálculo completo, um histórico e a exportação em HTML/JSON. Nenhum teste de desempenho é executado. |
| Relatórios | Histórico com busca, filtros e ordenação; detalhe com gráficos sincronizados (zoom, seleção, tooltip), histograma, linha do tempo de eventos e lacunas; comparação A/B com avisos de condições diferentes; exportação CSV/JSON/HTML. |
| Personalização | 8 temas, editor de tema, overlay (6 estilos, posição, monitor, escala, cores, métricas), editor do painel, preferências de interface. Importação/exportação versionada e validada. |
| Configurações | Inicialização, bandeja, fechamento, perfis de desempenho, PresentMon, alertas, atalhos globais, retenção de dados. |

## Instalar

O `Setup.exe` **não** vai no Git (é um instalador de ~83 MB gerado a cada publicação). Os usuários baixam o instalador na página de releases:

**[Baixar FRAMEX (Setup.exe)](https://github.com/gdapazz/FrameX/releases/latest)**

Execute o `Setup.exe`, aceite o administrador e abra o FRAMEX. O instalador coloca o programa em `Program Files`, inclui o PresentMon e instala o serviço/driver para FPS e temperatura da CPU. Veja [docs/EXECUCAO.md](docs/EXECUCAO.md).

Para gerar de novo depois de alterar o código: `./scripts/publish.ps1` (recria `D:\Framex\Setup.exe`). Quem desenvolve a partir do código:

```powershell
# Requer o SDK do .NET 10
./scripts/build.ps1
./scripts/test.ps1
dotnet run --project src/Framex.App
```

## Documentação

- [Arquitetura](docs/ARQUITETURA.md)
- [Decisões técnicas](docs/DECISOES.md)
- [Metodologia das métricas](docs/METRICAS.md)
- [Nota do Setup: fontes, fórmulas e limitações](docs/NOTA-DO-SETUP.md)
- [Compilação, publicação e instalador](docs/COMPILACAO.md)
- [Execução e configuração](docs/EXECUCAO.md)
- [Diagnóstico](docs/DIAGNOSTICO.md)
- [Testes](docs/TESTES.md)
- [Plano de benchmark](docs/BENCHMARK.md)
- [Componentes de terceiros](THIRD-PARTY-NOTICES.md)

## Limitações conhecidas

- Temperatura, clock e consumo da CPU exigem o driver PawnIO. O `Setup.exe` o instala e registra o serviço FRAMEX; depois disso o app lê esses sensores sem administrador. Sem o instalador, mesmo como administrador a temperatura fica indisponível. O Windows não oferece essa leitura sem driver — o FRAMEX não usa a zona térmica ACPI, que nesta máquina não reage à carga da CPU.
- FPS no jogo, com o FRAMEX instalado, é capturado pelo serviço (PresentMon incluído). Sem o instalador, é preciso o PresentMon em `%LOCALAPPDATA%\FRAMEX\tools` e permissão ETW.
- Jogos em tela cheia exclusiva podem cobrir o overlay; use modo janela sem bordas.
- A compatibilidade de sensores varia conforme hardware e driver.
- A Nota do Setup se baseia em medições de renderização (Blender Cycles), não em jogos. Ela não tem dados *single-thread* de CPU e compara GPUs de fabricantes diferentes por backends diferentes.
