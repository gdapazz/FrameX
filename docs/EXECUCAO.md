# Execução e configuração

## Primeira execução

1. Instale pelo `Setup.exe` e abra o FRAMEX **sem** administrador. O painel começa a mostrar CPU, GPU, RAM e VRAM em poucos segundos.
2. O instalador registra o serviço **FRAMEX** (`FramexService`, LocalSystem), inclui o PresentMon e instala o driver PawnIO. Com isso, temperatura/clock/consumo da CPU e FPS no jogo ficam disponíveis só com abrir o app.
3. Sensores sem leitura aparecem como “—” e têm o motivo na página **Hardware**: hardware não suportado, serviço parado, driver ausente etc.

## PresentMon (FPS e frametime)

Com o FRAMEX instalado pelo `Setup.exe`, **não é preciso baixar nem configurar o PresentMon**. O serviço executa a cópia em `{app}\tools` e a captura ETW, sem o app precisar de administrador.

Quem roda a partir do código-fonte (sem o instalador):

1. Baixe o **PresentMon console 2.x** (`PresentMon-2.x.x-x64.exe`) em <https://github.com/GameTechDev/PresentMon/releases>.
2. Coloque o executável em `%LOCALAPPDATA%\FRAMEX\tools` (não na pasta `tools` do repositório) ou indique o caminho em **Configurações → Captura e jogos**.
3. Conceda permissão ETW: execute o FRAMEX como administrador, ou adicione o usuário ao grupo **Performance Log Users** e faça logoff/logon:
   ```powershell
   # PowerShell como administrador
   Add-LocalGroupMember -SID 'S-1-5-32-559' -Member $env:USERNAME
   ```

A barra superior mostra o estado: não configurado, sem permissão, aguardando quadros, capturando. O PresentMon só é iniciado para o PID do jogo detectado e é encerrado ao fim da sessão.

## Temperatura da CPU

O Windows **não** expõe a temperatura do processador para um aplicativo comum. Sem um driver de kernel, o valor ACPI de “zona térmica” nesta máquina ficou fixo (~30 °C) mesmo com a CPU a 100% e **não é usado**.

O Setup.exe instala o **PawnIO** (driver assinado, GPL-2.0), o mesmo que o LibreHardwareMonitor usa. O serviço FRAMEX lê esses sensores e envia as leituras ao app. Sem o instalador, mesmo como administrador a temperatura fica indisponível se o PawnIO não estiver instalado.

## Detecção de jogos

- **Jogos da biblioteca:** executáveis associados manualmente ou confirmados. A sessão começa sozinha quando o processo aparece.
- **Candidatos:** processos com janela visível dentro de pastas de bibliotecas conhecidas (`steamapps\common`, `Epic Games`, `GOG Galaxy\Games`, `GOG Games`, `XboxGames`, `Ubisoft Game Launcher\games`, `EA Games`, `Riot Games`, `Games`). Aparecem em **Jogos → Pendentes** para confirmar ou ignorar. Launchers, serviços de antitrapaça e auxiliares conhecidos nunca são candidatos.
- **Associação manual:** **Jogos → Adicionar em execução** lista os processos com janela que ainda não estão na biblioteca. **Adicionar por arquivo** permite escolher o `.exe`.
- **Fim da sessão:** 10 s depois que o processo fecha. Um reinício nesse intervalo continua a mesma sessão. Sessões com menos de 20 s são descartadas.

## Overlay

- **Atalho padrão:** `Ctrl+Shift+F10` mostra ou oculta. `Ctrl+Shift+F8` entra no modo de edição: arraste e use o atalho de novo para fixar.
- Estilos, posição (cantos rápidos ou arrastar), monitor, escala, cores, opacidade e métricas ficam em **Personalização → Overlay**.
- No modo passivo, os cliques atravessam o overlay e ele nunca recebe foco.
- Em tela cheia exclusiva, o overlay pode ficar escondido; use janela sem bordas.

## Atalhos globais padrão

| Ação | Atalho |
|---|---|
| Mostrar/ocultar overlay | Ctrl+Shift+F10 |
| Mostrar/ocultar janela | Ctrl+Shift+F11 |
| Pausar/retomar gravação | Ctrl+Shift+F9 |
| Modo de edição do overlay | Ctrl+Shift+F8 |

Os atalhos podem ser alterados em **Configurações → Atalhos**. Se outro programa já usa a combinação, o FRAMEX avisa e mantém o atalho desativado.

## Bandeja, inicialização e fechamento

- **Iniciar com o Windows:** cria o valor `FRAMEX` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com `--minimized`. Desativar a opção remove o valor.
- **Ao fechar a janela:** perguntar (com “lembrar minha escolha”), minimizar para a bandeja ou encerrar.
- O menu da bandeja abre o FRAMEX, alterna o overlay, pausa a gravação, abre o último relatório e encerra.

## Perfis de desempenho

| Perfil | Sensores | Interface | Overlay (mín.) | Processos |
|---|---|---|---|---|
| Economia | 2 s | 2 s, sem gráficos ao vivo | 1000 ms | 4 s |
| Equilibrado (padrão) | 1 s | 1 s | 500 ms | 2 s |
| Detalhado | 0,5 s | 0,5 s | 250 ms | 2 s |

## Dados e privacidade

| O quê | Onde |
|---|---|
| Banco (sessões, jogos, configurações) | `%LOCALAPPDATA%\FRAMEX\framex.db` |
| Logs | `%LOCALAPPDATA%\FRAMEX\logs` (14 dias, 10 MB por arquivo) |
| PresentMon do usuário (execução sem instalador) | `%LOCALAPPDATA%\FRAMEX\tools` |
| PresentMon e serviço (instalação) | `{app}\tools` e `{app}\service` |
| Logs do serviço | `%ProgramData%\FRAMEX\service-logs` |
| Exportações | `Documentos\FRAMEX\Relatórios` (padrão) |

- A **retenção** fica em **Configurações → Relatórios e dados**. Ela apaga apenas as amostras detalhadas antigas; o resumo das sessões é mantido.
- Os arquivos exportados (CSV, JSON, HTML) não contêm caminhos nem nome de usuário. O HTML funciona offline.
- Nada é enviado pela rede.

## Argumentos de linha de comando

| Argumento | Efeito |
|---|---|
| `--minimized` | Inicia na bandeja (usado pela inicialização com o Windows). |
| `--after-restart` | Uso interno ao reiniciar como administrador: aguarda a instância anterior encerrar. |
