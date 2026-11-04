# FrontLine Lyrics — 3 scripts para Snap / loja

**Pré-requisitos (uma vez):** Ubuntu amd64, `.venv` do backend criada com PyInstaller e requisitos Linux instalados, .NET SDK compatível, `dpkg-deb`, Snapcraft e LXD/Snapcraft configurados. Rode na raiz do repositório:

```bash
chmod +x FrontLine-Linux/packaging-deb/*.sh FrontLine-Linux/packaging-snap/*.sh
```

## 1. Criar o Snap (local)
```bash
./FrontLine-Linux/packaging-snap/build-snap.sh 1.0.0
```
Produz `FrontLine-Linux/packaging-snap/frontline-lyrics_1.0.0_amd64.snap`.

**Testar** (em computador sem o Snap instalado):
```bash
sudo snap install ./FrontLine-Linux/packaging-snap/frontline-lyrics_1.0.0_amd64.snap --dangerous
sudo snap connect frontline-lyrics:audio-record
snap run frontline-lyrics
```
Se já tiver o mesmo Snap instalado, gerencie a instalação de teste manualmente; `snap remove` pode afetar dados locais.

## 2. Primeiro envio à Snap Store
```bash
./FrontLine-Linux/packaging-snap/publish-first.sh 1.0.0
```
Faz login, registro (se necessário), build e upload para **edge**, após confirmação. Login e registro exigem interação humana. A revisão da loja pode demorar ou pedir alterações.

## 3. Atualização do aplicativo
```bash
./FrontLine-Linux/packaging-snap/publish-update.sh 1.0.1
```
Compila 1.0.1, atualiza versão no `snapcraft.yaml` e faz upload para **edge** após confirmação.

## 4. Promover versão testada a stable

**Não é automático**: primeiro teste a revisão do edge, use `snapcraft revisions frontline-lyrics` para achar o número da revisão e publique-a com:
```bash
snapcraft release frontline-lyrics NUMERO_DA_REVISAO stable
```
Para publicar em stable, confirme `grade: stable` em `FrontLine-Linux/packaging-snap/snap/snapcraft.yaml` **antes da build e do upload** e só faça isso depois de o aplicativo estar apto para produção. `grade: devel` não é publicável em stable.

## Notas
- O `.deb` é um intermediário obrigatório neste fluxo; não remova `packaging-deb`.
- O número de versão **não** precisa mais ser alterado manualmente em `build-deb.sh`, `prepare-snap.sh` ou `snapcraft.yaml`.
- A aplicação construída recebe `-p:Version` no `dotnet publish`; mudanças de versão constantes no `.csproj` não são feitas automaticamente.
- O confinamento estrito exige testes reais de MPRIS, captura de áudio, reconhecimento, janela, tradução e persistência; empacotar não prova funcionamento.
- Não inclua os artefatos `payload/`, `parts/`, `prime/`, `stage/`, `*.snap`, `dist-linux/` no Git.
