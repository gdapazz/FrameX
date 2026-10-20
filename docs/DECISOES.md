# Decisões técnicas

Cada decisão traz o motivo e a alternativa descartada.

## .NET 10 + WPF

- **Escolha:** WPF sobre .NET 10 (LTS), x64.
- **Motivo:** é nativo de Windows, renderiza com aceleração por GPU, dá controle total de janelas (overlay transparente, sem foco, `WS_EX_TRANSPARENT`) e permite desenho próprio de alto desempenho com `OnRender`. A UI não precisa de dependências de terceiros.
- **Descartado:** WinUI 3, cujo suporte a janelas em camadas e topmost sem ativação é limitado; Electron, pelo consumo de memória incompatível com uma ferramenta que roda junto do jogo.

## Gráficos próprios em vez de biblioteca

As séries precisam de lacunas reais (NaN), sincronização de zoom, seleção e hover entre vários gráficos, e custo previsível no overlay. Controles desenhados com `DrawingContext` (`TimeSeriesChart`, `LiveChart`, `HistogramChart`, `ArcGauge`) fazem isso sem alocações por quadro e sem acrescentar licenças.

## PresentMon como processo separado

- **Motivo:** o PresentMon é a referência de mercado para frametime via ETW e não toca no processo do jogo, o que evita conflitos com antitrapaça.
- **Distribuição:** o executável **não é distribuído nem baixado** pelo FRAMEX. O usuário o obtém no repositório oficial, o que evita downloads silenciosos de executáveis e mantém o usuário no controle.
- **Ciclo de vida:** o processo roda dentro de um *Job Object* com `KILL_ON_JOB_CLOSE`, então não fica órfão nem se o FRAMEX cair. Ao parar, a sessão ETW nomeada `FRAMEX_Capture` também é encerrada.
- **Descartado:** consumir ETW diretamente (TraceEvent). Reimplementar a correlação de eventos do PresentMon seria extenso e mais propenso a erro.

## LibreHardwareMonitorLib

Biblioteca madura e com licença compatível (MPL-2.0, usada sem modificação). Sem administrador, o driver de baixo nível não carrega e temperatura, clock e consumo da CPU ficam indisponíveis. O FRAMEX **não pede elevação automaticamente**: mostra o motivo e oferece um botão explícito “Reiniciar como administrador”.

## SQLite local

- WAL, transações por lote e `synchronous=NORMAL`. Em caso de queda, perde-se no máximo o último lote, alguns segundos.
- Migrações por `PRAGMA user_version`, com backup antes de migrar.
- Verificação de integridade na abertura. Um banco corrompido é isolado (`framex.db.corrompido-AAAAMMDD-HHMMSS`) em vez de apagado.
- Frametimes ficam em blobs `float32` por lote. Linhas por quadro multiplicariam o tamanho do banco.

## Instância única

O mutex `Local\FRAMEX.SingleInstance` e o evento `Local\FRAMEX.Activate` garantem uma instância por sessão de usuário: abrir de novo apenas traz a janela existente para frente. A reinicialização como administrador usa `--after-restart`, que espera até 15 s a instância anterior liberar o mutex.

## Overlay

Janela WPF separada, `Topmost`, `ShowActivated=False`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` e, no modo passivo, `WS_EX_TRANSPARENT` (cliques atravessam). Nunca rouba o foco do jogo. Funciona sobre jogos em janela e em janela sem bordas. Em tela cheia exclusiva, o Windows pode não compor janelas por cima; isso é uma limitação documentada, não contornada.

## Temas por tokens

Todas as cores, raios, tamanhos e opacidades vêm de recursos dinâmicos (`B.*`, `C.*`, `R.*`, `T.*`, `S.*`, `F.*`, `O.*`). Trocar de tema substitui os valores sem recriar a interface. Temas importados são JSON validado (versão do esquema, formato de cores, faixas numéricas) e nunca contêm código.

## Inicialização com o Windows

Usa o valor `FRAMEX` em `HKCU\...\Run`, visível em Gerenciador de Tarefas → Inicializar e removível por lá. Não usa tarefas agendadas, serviços nem outros mecanismos ocultos. O valor só é escrito quando o usuário ativa a opção.

## Logs

Serilog em JSON compacto (`%LOCALAPPDATA%\FRAMEX\logs\framex-AAAAMMDD.jsonl`), com rotação diária e retenção limitada. Os logs ficam na máquina e não são enviados para lugar nenhum.

## Testes sem hardware

Os provedores simulados (`TestSupport.cs`) existem só no projeto de testes. A ferramenta `tools/Framex.UiSmoke` usa sessões sintéticas **rotuladas** (“Sessão sintética (teste de UI)”) em um diretório temporário, apagado ao final; nada disso entra no aplicativo distribuído.
