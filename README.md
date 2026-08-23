# TradutorHub

Aplicativo desktop para pesquisar traduções e instalá-las diretamente em jogos da Steam, sem abrir outro instalador.

## O que esta base faz

- Lê automaticamente o `catalog.json` público hospedado no GitHub.
- Permite pesquisar o nome do jogo no topo da tela.
- Mostra uma mensagem clara quando ainda não existe tradução cadastrada.
- Detecta bibliotecas Steam padrão e bibliotecas adicionais do `libraryfolders.vdf`.
- Baixa somente pacotes ZIP declarados no catálogo.
- Suporta uma tradução com vários ZIPs, como o Lucius II.
- Valida o SHA-256 antes de extrair qualquer arquivo.
- Bloqueia caminhos perigosos dentro dos ZIPs.
- Faz backup dos arquivos originais antes de substituir.
- Detecta conflito com arquivos pertencentes a outra tradução.
- Permite atualizar e remover a tradução pelo próprio app.
- Atualiza o catálogo ao abrir e depois a cada 6 horas.
- Trata operações especiais de pacote, como anexar um payload a um arquivo existente.

O TradutorHub não executa arquivos `.exe`. O formato recomendado para cada projeto é um ou mais ZIPs com os arquivos da tradução e as regras de instalação no catálogo.

## Como executar

1. Dê duplo clique em `TradutorHub.bat`.
2. O aplicativo já aponta para:

~~~text
https://raw.githubusercontent.com/GabrielMichell/tradutor-hub-catalogo/main/catalog.json
~~~

3. Digite o nome do jogo no campo de busca.
4. Selecione a tradução encontrada.
5. Clique em `Detectar Steam` ou escolha manualmente a pasta do jogo.
6. Clique em `Instalar tradução`.

## Como cadastrar uma tradução ZIP

Cada item do `catalog.json` usa `packageType` `zip` ou `multi-zip`, uma lista de `assets` e regras `install`.

~~~json
{
  "id": "meu-jogo-ptbr",
  "game": "Nome do jogo",
  "language": "pt-BR",
  "version": "1.0.0",
  "packageType": "zip",
  "folderHints": ["Nome da pasta Steam"],
  "install": [
    { "from": "MeuJogo_Data", "to": "game" }
  ],
  "assets": [
    {
      "role": "package",
      "fileName": "Minha-Traducao-v1.0.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Minha-Traducao-v1.0.zip",
      "sha256": "HASH_SHA256_DO_ZIP"
    }
  ]
}
~~~

`from` é o caminho dentro do ZIP. `to: game` significa a pasta principal detectada do jogo. O app também encontra a pasta declarada mesmo que o ZIP tenha uma pasta externa com o nome da release.

Para várias partes, use vários assets com `role: package` e uma regra que copie o conteúdo extraído:

~~~json
{
  "packageType": "multi-zip",
  "install": [
    { "from": ".", "to": "game" }
  ],
  "assets": [
    {
      "role": "package",
      "fileName": "Dados-01.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Dados-01.zip",
      "sha256": "HASH_DA_PARTE_01"
    },
    {
      "role": "package",
      "fileName": "Dados-02.zip",
      "downloadUrl": "https://github.com/usuario/repositorio/releases/download/v1.0.0/Dados-02.zip",
      "sha256": "HASH_DA_PARTE_02"
    }
  ]
}
~~~

Para jogos que exigem uma operação especial, o catálogo aceita `operations` com `copy` ou `append`. O `append` pode declarar `alignment` e `expectedSize` para impedir que um patch seja aplicado sobre uma versão errada do arquivo.

## Arquivos locais

Configurações, cache, pacotes baixados e backups ficam em:

~~~text
%LOCALAPPDATA%\TradutorHub
~~~

O catálogo completo e um modelo editável estão em `catalog.json` e `catalog.example.json`.

## Segurança

- O app aceita somente URLs HTTPS do GitHub.
- Cada pacote deve declarar SHA-256 para validação de integridade.
- Os ZIPs são validados contra path traversal antes da extração.
- O app não executa EXE, DLL, script ou outro programa vindo da release.
- O backup é criado dentro da área local do TradutorHub antes da instalação.
- É necessário possuir uma cópia legítima do jogo e fechar o jogo antes de instalar.

