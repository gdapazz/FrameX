# FRAMEX — Avisos de terceiros

O FRAMEX utiliza os componentes de terceiros listados abaixo. Cada componente é distribuído sob sua própria licença;
os textos completos das licenças estão nos endereços indicados e nos pacotes NuGet correspondentes.
Nenhum código desses componentes foi modificado.

## Componentes distribuídos com o aplicativo

| Componente | Versão | Licença | Projeto |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit (dependência do LibreHardwareMonitorLib) | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD (dependência do LibreHardwareMonitorLib) | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp (dependência do LibreHardwareMonitorLib) | 2.6.4 | Apache-2.0 | https://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard (dependência do LibreHardwareMonitorLib) | 1.0.0 | MIT | https://github.com/mono/mono |
| Microsoft.Data.Sqlite / SQLitePCLraw | 10.0.x | MIT | https://github.com/dotnet/efcore · https://github.com/ericsink/SQLitePCL.raw |
| SQLite | (embutido via SQLitePCLraw) | Domínio público | https://www.sqlite.org/copyright.html |
| System.Management, System.IO.Ports e demais bibliotecas .NET | 10.0.x | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection / Logging | 10.0.x | MIT | https://github.com/dotnet/runtime |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog.Formatting.Compact | 4.2.0 / 10.0.0 / 7.0.0 / 3.0.0 | Apache-2.0 | https://github.com/serilog |
| .NET Runtime / WPF / Windows Forms | 10.0 | MIT | https://github.com/dotnet |

### MPL-2.0 (LibreHardwareMonitorLib, DiskInfoToolkit, RAMSPDToolkit-NDD)

Estes componentes são usados sem modificação, como bibliotecas separadas (arquivos .dll). O código-fonte de cada um
está disponível nos repositórios indicados acima. Texto da licença: https://mozilla.org/MPL/2.0/

### Apache-2.0 (Serilog, HidSharp)

Licenciados sob a Apache License, Version 2.0. Texto da licença: https://www.apache.org/licenses/LICENSE-2.0

### MIT

Copyright (c) .NET Foundation e colaboradores; Copyright (c) Microsoft Corporation; Copyright (c) Eric Sink;
Copyright (c) Xamarin Inc. e colaboradores do Mono; e respectivos autores.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Dados distribuídos com o aplicativo

| Dados | Licença | Origem |
|---|---|---|
| Blender Open Data: medianas do Blender Benchmark 4.5.0 (CPU, OptiX, HIP, oneAPI), obtidas em 20/10/2026 | CC0 1.0 (domínio público) | https://opendata.blender.org/ |

Esses dados são usados pela Nota do Setup. São incluídos somente o nome do dispositivo, a mediana e o número de envios,
sem modificação de valores. A licença CC0 não exige atribuição; a atribuição é feita por cortesia. Texto da licença:
https://creativecommons.org/publicdomain/zero/1.0/. A atualização opcional baixa a mesma consulta pública, somente quando
o usuário pede. Detalhes em [docs/NOTA-DO-SETUP.md](docs/NOTA-DO-SETUP.md).

## Programas distribuídos com o instalador (não embutidos no código do FRAMEX)

| Programa | Versão | Licença | Origem |
|---|---|---|---|
| PresentMon (console) | 2.6.0 | MIT — Copyright (c) Intel Corporation | https://github.com/GameTechDev/PresentMon |
| PawnIO (instalador oficial `PawnIO_setup.exe`) | 2.2.0 | GPL-2.0 | https://github.com/namazso/PawnIO · https://github.com/namazso/PawnIO.Setup |

O PresentMon é copiado para `{app}\tools` e executado pelo serviço FRAMEX (processo separado, associado a um Job Object,
encerrado junto com a captura). O FRAMEX não o modifica. Texto da licença: `{app}\licenses\PresentMon-LICENSE.txt`.

O PawnIO é um driver de kernel assinado, usado pelo LibreHardwareMonitor para ler temperatura, clock e consumo da CPU.
O Setup.exe executa o instalador oficial com `-install -silent`, sem alterar o arquivo. O FRAMEX **não** é um trabalho
derivado do PawnIO. Código-fonte: https://github.com/namazso/PawnIO. Textos: `{app}\licenses\PawnIO-LICENSE.txt` e
`{app}\licenses\PawnIO-SOURCE.txt`. O PawnIO **não** é desinstalado junto com o FRAMEX, porque outros programas podem
usá-lo; remova-o em “Aplicativos instalados” do Windows, se quiser.

Quem roda o FRAMEX sem o Setup.exe ainda pode apontar um PresentMon próprio em `%LOCALAPPDATA%\FRAMEX\tools`.

## Componentes usados apenas nos testes (não distribuídos)

xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet.collector (MIT).
