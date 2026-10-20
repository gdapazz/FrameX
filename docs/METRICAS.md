# Metodologia das métricas

Este documento descreve exatamente como o FRAMEX calcula cada número. O código de referência está em `src/Framex.Core/FrameCapture/FrameStatistics.cs`, `LiveFrameAggregator.cs` e `src/Framex.Core/Reporting/SessionStatisticsCalculator.cs`.

## Origem dos dados

| Dado | Fonte |
|---|---|
| Frametime | Coluna `MsBetweenPresents` do PresentMon (eventos ETW de apresentação), um valor por quadro apresentado pelo processo monitorado. Em versões que não emitem essa coluna, usa `MsBetweenAppStart` ou `FrameTime`. |
| CPU, GPU, temperaturas, clocks, consumo, VRAM | LibreHardwareMonitorLib. |
| RAM | `GetPerformanceInfo` do Windows. |

O FRAMEX nunca gera FPS a partir de outra métrica. Sem PresentMon, FPS e frametime ficam ausentes.

## Validação

- **Frametime válido:** finito, > 0 e ≤ 2000 ms. Os demais são descartados e contados em “quadros inválidos” (o relatório informa a quantidade).
- **Sensores:** valores fora da faixa física por unidade são rejeitados e tratados como ausentes: % 0–100,5; °C −40–150; MHz 0–15 000; W 0–3 000; RPM 0–25 000; FPS 0–10 000; ms 0–60 000.
- **Ausente ≠ zero.** Uma leitura ausente é armazenada como ausente (NaN nos gráficos, “—” na interface) com o motivo, como “sensor não exposto pelo driver” ou “requer administrador”. O LibreHardwareMonitor informa 0 W para o consumo da CPU quando não tem acesso aos MSRs; o FRAMEX trata esse caso como indisponível.

## Estatísticas de quadros (sessão)

Sejam *N* quadros válidos com frametimes *tᵢ* (ms).

| Métrica | Definição |
|---|---|
| FPS médio | `1000 × N / Σtᵢ`, ou seja, o FPS real do período (média harmônica). Não é a média dos FPS instantâneos, que superestima o desempenho quando há picos. |
| 1% low | `1000 / média(maiores ⌈1% × N⌉ frametimes)`. Exige N ≥ 100; abaixo disso, “não calculado”. |
| 0,1% low | `1000 / média(maiores ⌈0,1% × N⌉ frametimes)`. Exige N ≥ 1000. |
| Frametime médio | `Σtᵢ / N`. |
| P50/P95/P99/P99,9 | Percentil por interpolação linear entre postos: posto `p/100 × (N−1)`. |
| Pior quadro | Maior frametime válido. |
| FPS mín./máx. | Extremos do FPS das **janelas completas de 1 s**: os quadros são acumulados em ordem até somarem ≥ 1000 ms, e cada janela fecha com `1000 × quadros / soma`. Um único quadro lento não vira “FPS mínimo 2”; ele aparece no pior quadro e nos lows. |
| Histograma | Faixas de frametime com limites 0, 4, 6, 8,33, 11,11, 16,67, 20, 25, 33,33, 50, 100 e 2000 ms (equivalentes a 250, 166, 120, 90, 60, 50, 40, 30, 20 e 10 FPS). |

> Definições de “1% low” variam entre ferramentas (algumas usam o percentil 99 do frametime). O FRAMEX usa a média do 1% mais lento e mostra também o P99, para que os valores possam ser comparados com outras ferramentas.

## Métricas ao vivo (painel e overlay)

- **FPS atual:** quadros do último segundo divididos pela soma dos seus frametimes.
- **1% low ao vivo:** mesma fórmula da sessão sobre os últimos 30 s.
- **Sem quadros por 2 s:** o FPS ao vivo passa a ser exibido como ausente.

## Séries temporais do relatório

- Os quadros são agrupados em janelas de 1 s alinhadas ao relógio. Cada segundo grava FPS, frametime médio, frametime máximo e contagem de quadros.
- Os sensores de hardware são gravados no intervalo do perfil: Economia 2 s, Equilibrado 1 s, Detalhado 0,5 s.
- A tabela “Métricas” do relatório mostra média aritmética, mínimo e máximo das amostras válidas, além da **cobertura**: amostras × intervalo nominal / duração. Para as métricas de quadros, a média da tabela é a média entre segundos, não ponderada por quadro; o “FPS médio” do topo do relatório é o harmônico, descrito acima.

## Lacunas

- **Hardware:** um intervalo sem amostras maior que `max(3 × intervalo nominal, 5 s)`.
- **Quadros:** um intervalo maior que 5 s sem segundos com quadros, entre o primeiro e o último segundo capturado.
- Nos gráficos, a linha é interrompida quando dois pontos consecutivos estão a mais de 5 s um do outro, ou quando o valor está ausente. Nunca há interpolação sobre a lacuna. O relatório também lista as lacunas, com início, fim e escopo.

## Quedas de desempenho

Um evento “queda de desempenho” é registrado quando há **≥ 3 segundos consecutivos** com FPS abaixo de **60% da mediana** da sessão. Segundos com mais de 2 s de distância entre si não contam como consecutivos. A detecção só roda com ≥ 30 s de FPS e mediana ≥ 20. O evento **não atribui causa**. A linha do tempo mostra o que ocorreu no mesmo período, como um alerta de temperatura, mas isso é apenas correlação temporal.

## Comparação de sessões

- A variação é `B − A`, em valor absoluto e percentual.
- As cores só indicam melhora ou piora para FPS e frametime, e só quando a variação é ≥ 1%. Temperatura, uso e consumo não são classificados como “bons” ou “ruins”.
- Um aviso aparece quando as condições diferem: jogos diferentes, hardware detectado diferente, resolução do monitor principal diferente, perfil de monitoramento diferente ou sessão incompleta. O aviso sempre lembra que o FRAMEX não conhece as configurações gráficas nem a versão do jogo.

## Alertas

Uma regra dispara quando a condição permanece verdadeira pela **duração mínima** configurada e respeita o **intervalo de repetição**. As regras podem valer para um jogo específico ou para todos. O FRAMEX não define limites “seguros” universais: os valores padrão são pontos de partida editáveis, e o texto do alerta descreve apenas o valor observado.
