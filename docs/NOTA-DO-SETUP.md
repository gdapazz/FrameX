# Nota do Setup

Submenu **Monitoramento → Nota do Setup**. Ele identifica CPU, GPU, RAM e armazenamento localmente, procura CPU e GPU em
bases públicas de medições e calcula uma nota inteira de 0 a 1000 com critérios documentados (metodologia **1.0**).

O FRAMEX **não executa nenhum teste de desempenho** neste recurso. As notas de CPU e GPU usam medianas públicas de
renderização (Blender Cycles), enviadas por outros usuários. As notas de RAM e armazenamento são uma **estimativa técnica**
calculada a partir da capacidade.

## 1. Fontes

### Integrada

| Base | Conteúdo | Licença | Obtida em | Registros válidos | SHA-256 da cópia incluída |
|---|---|---|---|---:|---|
| `blender-4.5.0-cpu` | Mediana do Blender Benchmark 4.5.0 por CPU | CC0 1.0 | 20/10/2026 | 1212 | `996f826503fc22ee136f90eedf7a6ecb5d757f2d9814be6ab71dd35750eaa134` |
| `blender-4.5.0-optix` | GPUs NVIDIA (PCI 10DE), backend OptiX | CC0 1.0 | 20/10/2026 | 270 | `0b16618d9add05bf8f8020813ffa9987233c593e33d32f81008916e667fe92a6` |
| `blender-4.5.0-hip` | GPUs AMD (PCI 1002), backend HIP | CC0 1.0 | 20/10/2026 | 81 | `e06adbde9164504b66ba16566efd8cab5a4b3ec600c7da2776e556ac66be4e48` |
| `blender-4.5.0-oneapi` | GPUs Intel (PCI 8086), backend oneAPI | CC0 1.0 | 20/10/2026 | 25 | `5aba712cde325ba9b7e27afe0fefba7fec5c72e3c3a7dcb368a9829d048a9ba8` |

- **Origem:** [Blender Open Data](https://opendata.blender.org/), consulta pública
  `https://opendata.blender.org/benchmarks/query/?blender_version=4.5.0&group_by=device_name&response_type=datatables&compute_type={CPU|OPTIX|HIP|ONEAPI}`.
- **Licença:** os dados do Blender Open Data são publicados sob [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
  Isso permite incluir uma cópia no aplicativo; a atribuição é feita por cortesia.
- **Campos usados:** nome do dispositivo, mediana da pontuação e número de envios. Nenhum outro dado é armazenado.

### Investigadas e não integradas

| Fonte | Motivo |
|---|---|
| TechPowerUp GPU Database e o pacote `dbgpu`, que a reproduz | Os termos do site proíbem a extração automatizada. |
| RightNow-GPU-Database | Não integrada. Esta versão usa somente medições com licença de redistribuição verificada (CC0). |
| Dois repositórios públicos de especificações de CPU | Não têm arquivo de licença, então a redistribuição não é permitida. |

Nenhuma base de especificações (clocks, núcleos ou VRAM de catálogo) foi integrada. Essas informações vêm do próprio
Windows e aparecem apenas como fatos do componente: **não alteram a nota**.

## 2. Identificação local

Tudo é feito offline, sem privilégios de administrador e sem enviar dados.

| Categoria | Fonte | Observações |
|---|---|---|
| CPU | `Win32_Processor` | Nome, fabricante, núcleos, threads, clock máximo e arquitetura. Com mais de um soquete, um aviso informa que só o primeiro processador é considerado. |
| GPU | `Win32_VideoController` + registro do driver | A VRAM vem de `HardwareInformation.qwMemorySize`. `AdapterRAM` só é usado abaixo de 4 GiB, porque o campo é de 32 bits. Classificação: virtual (`ROOT\`, `SWD\`, Parsec, Remote Display, VMware…), dedicada, integrada ou desconhecida. |
| RAM | `GetPhysicallyInstalledSystemMemory` + `Win32_PhysicalMemory` | Capacidade instalada (firmware) conferida com a soma dos módulos. Também são lidos tipo SMBIOS, velocidade, fabricante e número de peça. |
| Armazenamento | `MSFT_PhysicalDisk` (`root\Microsoft\Windows\Storage`), com fallback para `Win32_DiskDrive` | Somente discos físicos, pelo **tamanho total** (nunca o espaço livre). Mídia e barramento são reportados pelo Windows; M.2 **não** é tratado como NVMe. |

### Regras de GPU

- Adaptadores virtuais são ignorados.
- A GPU é escolhida automaticamente apenas quando não há dúvida: um único adaptador elegível, uma única dedicada (sem
  desconhecidas) ou uma única integrada.
- Nos demais casos, o usuário escolhe. A escolha é salva localmente e pode ser desfeita.
- GPUs **nunca são somadas**.

### Regras de armazenamento

| Situação | Tratamento |
|---|---|
| USB, SD, MMC, virtual, iSCSI, rede ou mídia óptica | Excluída da soma. |
| Tamanho zero ou ausente | Excluída da soma. |
| Duplicata pelo identificador | Excluída da soma. |
| Interface desconhecida (sem evidência de unidade externa) | Incluída como "incerta", o que reduz a confiabilidade. |

Partições e volumes não são lidos, por isso não há contagem dupla. O total é exibido em GB/TB decimais ("nominal") e em
GiB/TiB. Números de série não são guardados: o identificador interno é um hash.

## 3. Correspondência com a base

O nome passa por uma **normalização estrita**: os símbolos ™, ® e © são removidos antes da forma NFKC, e o texto vai para
minúsculas, com espaços e pontuação compactados. Em seguida, a busca segue esta ordem:

| # | Situação | Estado | Pontua? |
|---|---|---|---|
| 1 | Base indisponível | `SourceUnavailable` | Não |
| 2 | Escolha manual salva e ainda presente na base | `UserSelected` | Sim (confiança máxima: média) |
| 3 | Nome local sem número de modelo (ex.: "AMD Radeon(TM) Graphics") | `IncompleteName` | Não |
| 4 | Igualdade estrita | `ExactMatch` | Sim |
| 5 | Igualdade após remover "@ 3.30GHz", "N-Core Processor", "CPU" e "Processor" (só para CPU) | `VerifiedAlias` | Sim |
| 6 | Registro da fonte rejeitado na validação | `InvalidRecord` | Não |
| 7 | Candidatos que contêm **todos** os tokens de modelo do nome local e têm similaridade de Jaccard ≥ 0,4 | `ProbableMatch` (1 candidato), `AmbiguousMatch` (vários) ou `NotFound` (nenhum) | Não. Os candidatos são oferecidos para escolha manual. |

Um modelo desconhecido **nunca** recebe a nota de um parecido; só uma confirmação explícita do usuário o inclui no
cálculo. Escolhas manuais ficam na tabela local `setup_overrides` e não alteram a base.

## 4. Pontuação (metodologia 1.0)

### Por categoria

- **CPU e GPU:** \(s = \dfrac{1000}{1 + e^{-k(\ln x - m)}}\), onde \(x\) é a mediana do Blender Benchmark. Os parâmetros
  \(m\) e \(k\) fazem as âncoras fixas resultarem em 50 e 950 pontos:

  | Categoria | 50 pontos | 950 pontos |
  |---|---:|---:|
  | CPU | 25 | 775 |
  | GPU | 120 | 9700 |

  As âncoras são aproximadamente os percentis 5 e 95 dos dispositivos com ≥ 3 envios em 20/10/2026, congelados nesta
  versão. Como a escala é logarítmica, a mesma razão de desempenho vale sempre a mesma distância de nota, e dobrar a
  medição **não** dobra a nota. A curva é limitada: cada duplicação soma no máximo \(1000 \cdot k \ln 2 / 4\) pontos.
- **RAM:** interpolação logarítmica sobre a capacidade em GiB, com estes pontos: 2 → 0, 4 → 150, 8 → 450, 16 → 750, 32 → 920 e 64 → 1000.
- **Armazenamento:** interpolação sobre a capacidade total em GB decimais. A curva é linear até 128 → 150 e depois passa por 256 → 350, 512 → 600,
  1000 → 800, 2000 → 920 e 4000 → 1000.
- **RAM e armazenamento:** tipo, frequência e interface são exibidos, mas não pontuam nesta versão, porque não há base
  licenciada que relacione esses dados a desempenho.

### Nota geral

\[ S = \operatorname{round}\left(\frac{\sum_i a_i\,w_i\,s_i}{\sum_i a_i\,w_i}\right) \]

- \(a_i\) vale 1 se a categoria foi pontuada.
- Pesos-base: GPU 45, CPU 35, RAM 12 e armazenamento 8. Sem armazenamento, os pesos ficam 48,91 / 38,04 / 13,04.
- O arredondamento é `MidpointRounding.AwayFromZero`, com resultado limitado a 0–1000.
- **Desativado ≠ desconhecido:**
  - armazenamento desmarcado sai do cálculo e aparece como "não participou";
  - categoria sem dados fica sem nota, nunca com zero.
- **Completude:**

  | Situação | Resultado |
  |---|---|
  | Todas as categorias ativas foram pontuadas | Completa |
  | Só o armazenamento ficou sem nota | Parcial (a nota é calculada com os pesos renormalizados) |
  | GPU, CPU ou RAM sem nota | Incompleta, **sem nota geral** |

- **Cobertura:** peso pontuado dividido pelo peso ativo.
- **Confiabilidade:** a menor entre as categorias pontuadas.

  | Categoria | Alta | Média | Baixa |
  |---|---|---|---|
  | CPU e GPU | ≥ 10 envios | ≥ 3 envios | Menos de 3 envios |
  | RAM | Firmware e módulos concordam (1%) | — | — |
  | Armazenamento | Nenhuma unidade incerta | — | — |

### Faixas

| Nota | Classificação |
|---|---|
| 0–199 | Entrada |
| 200–399 | Básico |
| 400–549 | Intermediário |
| 550–699 | Avançado |
| 700–849 | Alto desempenho |
| 850–1000 | Entusiasta |

## 5. Cache e atualização

As bases são lidas nesta ordem:

1. cache em `%LOCALAPPDATA%\FRAMEX\datasets` (`{id}.json` e `{id}.meta.json`, esquema 1, SHA-256 conferido);
2. cópia incluída no FRAMEX (SHA-256 conferido);
3. "indisponível".

**Nenhum valor é inventado** quando uma fonte falha.

A atualização só acontece quando o usuário clica em **Atualizar bases** e confirma. Ela funciona assim:

- envia só a consulta pública da lista inteira; o inventário do computador não é enviado;
- usa apenas HTTPS no host fixo e não segue redirecionamentos;
- limita a resposta a 4 MB, com 30 s de tempo limite e até 3 tentativas com espera crescente;
- valida o esquema, rejeita linhas inválidas e exige pelo menos 15 registros;
- grava em `.tmp` e só então substitui o cache;
- mantém a cópia anterior se a nova falhar;
- detecta conteúdo idêntico e não regrava;
- não repete em laço; uma nova avaliação passa a usar a nova cópia.

## 6. Histórico e exportação

- Cada avaliação é salva no SQLite (`setup_evaluations`, migração 3) com o SHA-256 do conteúdo.
  - Um registro corrompido é ignorado.
  - Avaliações antigas são reabertas sem nova consulta e mantêm a metodologia original.
- O histórico guarda modelos e capacidades, **não** guarda caminhos, nome de usuário, números de série ou nome do computador.
- Exportação em HTML autocontido ou JSON versionado (`framex-setup-score`, v1).

## 7. Limitações

- **Render não é jogo:** o Blender Benchmark mede renderização Cycles. A nota indica o potencial de processamento, não FPS em jogos.
- **CPU:** não há base licenciada de desempenho *single-thread*. A nota reflete desempenho multinúcleo, o que favorece CPUs com muitos núcleos.
- **GPU entre fabricantes:** OptiX, HIP e oneAPI são backends diferentes, então a comparação entre fabricantes tem viés.
- **Cobertura:** GPUs integradas e modelos raros podem não existir na base ou ter poucos envios (confiabilidade baixa).
- **RAM e armazenamento:** a nota usa só a capacidade.
- **Cache:** a troca dos dois arquivos do cache não é atômica em conjunto. Se eles divergirem, o SHA falha e a cópia incluída é usada.
