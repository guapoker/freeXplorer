# Port para Linux: onde estamos

Branch: `linux-cli`. Última atualização: 13/10/2026.

## Feito

### 1. CLI no Linux (commit `4ee7d87`)

- `FreeXplorer.Core` acessa discos físicos no Linux:
  - `Storage/Libc.cs`: chamadas à libc (`open`, `pread`, `pwrite`, `fsync`, `ioctl`, `stat`, `lchown`).
  - `Storage/LinuxDisks.cs`: lista discos via `/sys/block` (modelo, serial, barramento USB/SATA/NVMe, removível). Detecta o disco do sistema (`/`, `/boot`, `/home`…) e discos com partições montadas, em swap ou presas em LVM/LUKS.
  - `Storage/PhysicalDiskDevice.cs`: `OpenLinux` abre `/dev/sdX`. Na escrita usa `O_EXCL`, então o kernel recusa se houver partição montada.
  - `Storage/FileOwner.cs`: arquivos criados sob `sudo` voltam para o usuário que rodou o `sudo`.
  - `Storage/DriveBackup.cs`: checagem do destino do backup acha o ponto de montagem certo e sabe quais sistemas de arquivos aceitam arquivo esparso (ext4, btrfs, xfs…).
- A CLI compila como `net10.0` fora do Windows (`net10.0-windows` continua no Windows).
- Alvos: `disk:N` ou `/dev/sdb`, `/dev/disk/by-id/...`. Um caminho em `/dev/` nunca é aberto como imagem.
- `build.sh`: equivalente ao `build.ps1`, gera `dist/freeXplorer-cli-<versão>-linux-x64.tar.gz`.
- Correção geral: `Size.Format` usa cultura invariante ("1.5 KB" em qualquer idioma do sistema).

### 2. Mount via FUSE (commit `8348fc5`)

- Novo projeto `src/FreeXplorer.Fuse`:
  - `FuseProtocol.cs`: estruturas do protocolo do kernel (`fuse.h`, versão 7.31). Não depende da libfuse.
  - `FatxFuse.cs`: traduz cada requisição FUSE numa chamada ao `FatxVolume`. É uma função pura (bytes de entrada → resposta), testada em `tests/FreeXplorer.Tests/FuseTests.cs`.
  - `FuseMount.cs`: monta via `fusermount3` (funciona sem root para imagens), recebe o descritor de `/dev/fuse` pelo socket e atende com 4 threads.
- Ponto de montagem padrão: `/media/<usuário>/<nome>` com `sudo`, e `~/FATX/<nome>` sem. `--folder` escolhe outro.
- Somente leitura por padrão, `--rw` para escrever.
- SIGTERM e SIGHUP encerram como o Ctrl+C, desmontando direito. Um `umount` feito por fora também encerra o comando.
- O AppArmor do Ubuntu impede o `auto_unmount` do `fusermount3`. Por isso, após um `kill -9`, a pasta fica "pendurada", e montar de novo no mesmo lugar limpa.

### Testes

- `dotnet test tests/FreeXplorer.Tests`: 244 passam no Linux. Os testes da janela WPF e do WinFsp ficam de fora fora do Windows.
- No HD real (Hitachi 250 GB via USB) foram testados `list`, `info`, `ls`, `get` e `fsck`, todos só leitura, e funcionaram. A escrita no NVMe do sistema foi bloqueada como esperado.
- O mount foi testado com escrita apenas em imagem de prática: criar, copiar, renomear, apagar e conferir o hash depois de desmontar.

## Falta

1. **Testar o mount no HD real.** O HD foi desconectado antes desse teste. Primeiro só leitura:
   ```
   sudo src/FreeXplorer.Cli/bin/Debug/net10.0/freexplorer-cli mount /dev/sda content
   ```
   Conferir que aparece no gerenciador de arquivos e que dá para abrir e copiar arquivos. Depois, com um backup feito antes, testar `--rw`.
2. **Testar escrita no HD real** (`put`, `mkdir`, `rm`, `format`, `restore`). Até agora só foram testados com imagens. Fazer `backup` antes (precisa de até ~205 GB livres).
3. **Interface gráfica para Linux.** A janela atual é WPF e só roda no Windows. Opções:
   - Avalonia, que tem XAML parecido. Os ViewModels de `src/FreeXplorer.App/ViewModels` dão para aproveitar em boa parte.
   - Ou uma interface diferente, como já conversamos.
4. **Publicar uma release Linux:** rodar `./build.sh` e `./build.sh --self-contained` e anexar os `.tar.gz`.
5. **Rodar os testes no Windows** para confirmar que nada mudou lá. As mudanças foram feitas para não afetar o Windows, mas isso não foi conferido numa máquina Windows.
6. Opcional: mandar as mudanças para o repositório original (AlvaroEPena/freeXplorer) via pull request.

## Para retomar

```
git checkout linux-cli
dotnet build tests/FreeXplorer.Tests
dotnet test tests/FreeXplorer.Tests
```
O .NET 10 SDK foi instalado via `apt install dotnet-sdk-10.0`.
