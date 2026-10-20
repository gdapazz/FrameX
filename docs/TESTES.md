# Testes

## Como executar

```powershell
./scripts/test.ps1              # testes unitários e de integração (xUnit)
./scripts/test.ps1 -UiSmoke     # + renderização offscreen de todas as telas
```

Os resultados (TRX) ficam em `artifacts/test-results`, e as imagens das telas em `artifacts/ui-smoke`.

## Suíte `tests/Framex.Tests` (234 casos, 147 deles da Nota do Setup)

| Arquivo | Cobertura |
|---|---|
| `FrameStatisticsTests` | FPS médio harmônico; percentis por interpolação; 1%/0,1% low e mínimos de amostras; descarte de quadros inválidos; janelas de 1 s; histograma; agregadores ao vivo (ociosidade e memória fixa). |
| `MonitoringTests` | Conversões de unidade; “—” em vez de zero; validação de faixas; isolamento de provedor com falha e *backoff*; prioridade entre provedores com fallback; sensor desconectado (obsoleto → indisponível); métrica desconhecida não vira zero; mesclagem de inventário. |
| `GameDetectionTests` | Jogo conhecido e processo principal; launchers e auxiliares ignorados; heurística exige biblioteca de loja e janela visível; ignorados/removidos; início/fim com período de carência; reinício dentro da carência mantém a sessão; travamento encerra a sessão; múltiplas instâncias; candidato vira pendente de confirmação. |
| `SessionRecorderTests` | Gravação progressiva e resumo; sessão curta descartada; recuperação de sessão interrompida como incompleta; falha de captura não interrompe o hardware; buffers limitados em sessões longas. |
| `PersistenceTests` (SQLite real em pasta temporária) | Migração até a última versão; migração v1 → v2 preservando o histórico, com backup; esquema mais novo rejeitado sem tocar nos dados; banco corrompido isolado; ida e volta de sessão completa; filtros e ordenação; exclusão em cascata; retenção que mantém o resumo; jogos e ignorados; configurações e histórico de alertas. |
| `AlertTests` | Duração mínima; interrupção zera o contador; cooldown; regra desativada ou fora de sessão; escopo por jogo; FPS ausente não dispara; VRAM %; alertas de evento; regras de temperatura **desativadas por padrão**. |
| `ReportingAndCustomizationTests` | Lacunas; cobertura e métricas ausentes; detecção de queda sem afirmar causa; quedas curtas ignoradas; CSV/JSON/HTML (HTML autocontido, texto escapado, sem caminhos pessoais); comparação e avisos; temas e pacotes (ida e volta, cor inválida, pacotes inválidos e grandes demais, limites do overlay); normalização do painel; temas embutidos válidos e com contraste legível; *parser* do PresentMon (QPC, outros processos, linhas malformadas, quadros não exibidos, colunas v1). |

### Nota do Setup (`tests/Framex.Tests/SetupScore`)

| Arquivo | Cobertura |
|---|---|
| `MatchingTests` | Normalização (símbolos tipográficos e anotações documentadas de CPU); sufixos que distinguem modelos nunca são removidos; correspondência exata, por apelido verificado, provável, ambígua, não encontrada, registro inválido, base indisponível e nome sem número de modelo; nome parecido nunca é pontuado automaticamente; escolha manual só vale se o registro existir; duplicatas resolvidas de forma determinística; classificação de GPU e leitura do PCI ID; seleção de GPU (virtual e integrada ignoradas diante de uma dedicada; duas dedicadas ou tipo desconhecido exigem escolha; GPUs nunca somadas). |
| `StorageCapacityTests` | Soma pelo tamanho total (incluindo 256 GB NVMe + 120 GB SATA = 376 GB); HDD e SSD contados; capacidade inválida ou ausente; exclusão de rede, virtuais e removíveis; partições de um mesmo disco contadas uma vez; interface desconhecida marcada como incerta; GB × GiB; totais acima de Int32; RAID; mapeamento das enumerações do `MSFT_PhysicalDisk`; M.2 não implica NVMe. |
| `ScoringTests` | Pesos (armazenamento ≤ 8%) e renormalização sem armazenamento; fórmula conferida à mão; arredondamento e limites; âncoras e ordem da escala logística; dobrar a medição soma um incremento limitado; entradas inválidas rejeitadas; curvas de RAM e armazenamento; faixas sem lacunas; parcial × incompleta; "não participou" ≠ zero; RAM pela capacidade instalada e divergência sinalizada; tipo, frequência e VRAM não alteram a nota; base indisponível sem nota fictícia; fabricante de GPU sem base; escolha manual limitada a confiança média; versão da metodologia registrada; repetibilidade. |
| `DataSourceTests` | *Parser* do Blender Open Data (esquema, linhas inválidas com motivo, mínimo de registros, tamanho máximo); cópias incluídas com SHA-256 e dispositivos conhecidos; sem cache nem cópia → indisponível; atualização com HTTP simulado (sucesso e uso offline, conteúdo idêntico, resposta inválida mantém a cópia anterior, erro de rede com tentativas limitadas, redirecionamento não seguido, resposta grande demais, cancelamento); cache corrompido volta à cópia incluída; repositório SQLite (ida e volta, integridade, escolhas manuais); relatório sem números de série nem caminhos. |
| `EvaluationServiceTests` | Etapas em ordem e salvamento; opção de armazenamento congelada no início; cancelamento sem salvar; tempo limite do inventário, das bases e geral; sem bases → avaliação incompleta com explicações; exceção no inventário libera a avaliação; avaliação simultânea recusada; falha ao salvar mantém o resultado com aviso; histórico reaberto sem nova consulta. |
| `RealHardwareTests` | Opcional (`FRAMEX_REAL_HW=1`): executa o inventário e a avaliação reais neste computador. |
| `ServiceBridgeTests` | Pipe local entre app e serviço: ida e volta de sensores (incluindo cache), serviço ausente sem inventar dados, pedido desconhecido recusado, mensagem grande demais fecha só aquela conexão, captura transmite quadros e encerra ao desconectar, PID inválido recusado, segundo pedido substitui o anterior, mesclagem de sensores duplicados. |

Os provedores simulados ficam em `TestSupport.cs`, apenas no projeto de testes. Os testes de persistência usam um banco SQLite real em um diretório temporário e o apagam ao final.

## Verificação de UI (`tools/Framex.UiSmoke`)

Executa o aplicativo real (mesmo `AppHost`, mesmos serviços e sensores reais) com dados em um diretório temporário. A ferramenta:

1. Cria duas **sessões sintéticas rotuladas** (“Sessão sintética (teste de UI)”), com uma lacuna de 30 s, uma queda de FPS e resoluções diferentes.
2. Renderiza em PNG, sem exibir janelas: todas as páginas, os três estados da Nota do Setup (preparação, "Avaliando seu setup" e resultado com problemas e cálculo abertos, usando o hardware real e as bases incluídas, sem acesso à rede), as abas de Personalização (incluindo o editor de tema), as seções de Configurações, o detalhe do relatório, a comparação, o overlay e a janela principal.
3. Coleta os erros de binding do WPF (`PresentationTraceSources`). O código de saída é 1 se houver algum e 2 se houver exceção.
4. Não altera a inicialização com o Windows e apaga o diretório temporário ao final.

## O que não é coberto automaticamente

- **Captura real de FPS:** requer o PresentMon e permissão ETW, e não foi possível validá-la no ambiente de desenvolvimento. O *parser* e o fluxo estão testados com dados de formato real.
- **Sensores de CPU que dependem do driver** (temperatura, clock, consumo): requerem administrador.
- **Interação manual:** arrastar o overlay, atalhos globais dentro de jogos, notificações da bandeja. Veja o roteiro em [BENCHMARK.md](BENCHMARK.md).
