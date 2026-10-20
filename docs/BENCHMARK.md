# Plano de benchmark e validação

> **Este documento é um plano. Nenhum resultado foi medido ainda.** Preencha as tabelas somente com medições reais, registrando hardware, driver, versões e data.

## Objetivos

1. **Exatidão do FPS/frametime:** os números do FRAMEX coincidem com os do PresentMon puro e com uma ferramenta de referência (CapFrameX ou OCAT) na mesma captura.
2. **Custo do FRAMEX:** impacto do aplicativo e do overlay no FPS do jogo e no uso de CPU e memória.
3. **Robustez:** sessões longas, reinícios de jogo, troca de perfil e encerramento forçado.

## Ambiente a registrar

| Item | Valor |
|---|---|
| CPU / GPU / RAM | |
| Driver de vídeo | |
| Windows (versão/build) | |
| FRAMEX (versão) / PresentMon (versão) | |
| Resolução / taxa de atualização / modo de janela | |
| Plano de energia; V-Sync/limitador | |

## 1. Exatidão

**Procedimento:**

1. Use um jogo com benchmark interno repetível ou um trecho fixo de 60 s.
2. Rode o PresentMon puro (`--process_name <jogo>.exe --output_file ref.csv`) e o FRAMEX **na mesma execução**. Eles usam sessões ETW diferentes e não interferem entre si.
3. Calcule o FPS médio (harmônico), o 1% low (média do 1% mais lento) e o P99 a partir de `ref.csv` com a mesma metodologia de [METRICAS.md](METRICAS.md). Compare com o relatório exportado em JSON pelo FRAMEX.
4. Repita 3 vezes, em 3 cenários: GPU-bound, CPU-bound e FPS limitado (ex.: 60).

**Critério:** diferença ≤ 0,5% no FPS médio e ≤ 1% no 1% low e no P99. Diferenças maiores devem ser explicadas, por exemplo por quadros descartados ou pelo instante de início da captura.

| Cenário | Execução | FPS médio ref. | FPS médio FRAMEX | Δ | 1% low ref. | 1% low FRAMEX | Δ |
|---|---|---|---|---|---|---|---|
| | | | | | | | |

## 2. Custo de desempenho

**Configurações**, em ordem aleatória, com 5 execuções de 60 s cada:

- A: sem FRAMEX
- B: FRAMEX minimizado (perfil Equilibrado), sem overlay
- C: FRAMEX + overlay Compacto
- D: FRAMEX + overlay Detalhado (perfil Detalhado)
- E: FRAMEX perfil Economia + overlay Somente FPS

**Medir:**

- FPS médio e 1% low do jogo, com PresentMon puro como referência;
- CPU do processo `Framex` (média de `\Process(Framex)\% Processor Time` / núcleos);
- *working set* do FRAMEX e do PresentMon.

**Análise:** média ± desvio-padrão por configuração. Relate a diferença para A com intervalo de confiança de 95%. **Não declare impacto** se o intervalo incluir zero.

| Config. | FPS médio (média ± dp) | 1% low (média ± dp) | CPU FRAMEX (%) | Memória FRAMEX (MiB) |
|---|---|---|---|---|
| A | | | — | — |
| B | | | | |
| C | | | | |
| D | | | | |
| E | | | | |

## 3. Robustez (roteiro manual)

| Caso | Passos | Esperado | Resultado |
|---|---|---|---|
| Sessão longa | Jogar 2 h | Memória do FRAMEX estável (buffers limitados); relatório abre normalmente | |
| Reinício do jogo | Fechar e reabrir em < 10 s | Mesma sessão, evento na linha do tempo | |
| Travamento do FRAMEX | Encerrar `Framex.exe` pelo Gerenciador de Tarefas durante a sessão | Nenhum `PresentMon` órfão; na próxima abertura, a sessão aparece como incompleta com os dados até o último lote | |
| Sem permissão ETW | Remover o usuário do grupo e reiniciar | Estado “sem permissão” com instrução; hardware continua sendo gravado | |
| Troca de monitor do overlay | Mover o overlay para o 2º monitor | Posição preservada após reiniciar | |
| Atalho em jogo | Usar atalhos com o jogo em foco | Funcionam sem tirar o foco do jogo | |
| Tela cheia exclusiva | Jogo em tela cheia exclusiva | Documentar se o overlay aparece (limitação conhecida) | |
| Importação inválida | Importar tema/overlay corrompido | Erro claro; nada é aplicado | |
