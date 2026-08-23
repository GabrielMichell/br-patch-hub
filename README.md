# TradutorHub

Aplicativo desktop para listar traduções e abrir os instaladores oficiais publicados nas releases do GitHub.

## O que esta base faz

- Lê um catalog.json público hospedado no GitHub.
- Lista várias traduções em um catálogo modular.
- Baixa instaladores EXE e suas dependências ZIP quando necessário.
- Guarda todos os arquivos do mesmo pacote em uma pasta exclusiva.
- Valida o SHA-256 de cada arquivo antes de abrir o instalador.
- Usa cache por tradução e versão para não baixar novamente arquivos válidos.
- Abre o instalador oficial somente depois de uma confirmação.
- Atualiza o catálogo automaticamente ao abrir e depois a cada 6 horas.

O instalador continua responsável por detectar a pasta do jogo, instalar os arquivos, criar backups e remover a tradução. O TradutorHub não executa o EXE de forma silenciosa.

## Como executar

1. Dê duplo clique em TradutorHub.bat.
2. O aplicativo já vem apontando para:

~~~text
https://raw.githubusercontent.com/GabrielMichell/tradutor-hub-catalogo/main/catalog.json
~~~

3. Selecione uma tradução e clique em Baixar e abrir instalador.
4. Confirme a abertura do instalador oficial.

## Como cadastrar uma tradução

Cada item do catalog.json usa packageType installer e possui uma lista de assets. Um asset com role installer é o EXE que será aberto. Assets com role dependency são baixados para a mesma pasta antes da execução.

~~~json
{
  "id": "meu-jogo-ptbr",
  "game": "Nome do jogo",
  "language": "pt-BR",
  "version": "1.0.0",
  "packageType": "installer",
  "assets": [
    {
      "role": "installer",
      "fileName": "Meu-Jogo-Instalador.exe",
      "downloadUrl": "https://github.com/usuario/repo/releases/download/v1.0.0/Meu-Jogo-Instalador.exe",
      "sha256": "HASH_DO_EXE"
    },
    {
      "role": "dependency",
      "fileName": "Dados-01.zip",
      "downloadUrl": "https://github.com/usuario/repo/releases/download/v1.0.0/Dados-01.zip",
      "sha256": "HASH_DA_PARTE"
    }
  ]
}
~~~

Para um instalador sem partes, basta cadastrar apenas o asset com role installer. Para instaladores divididos, cadastre todas as partes. O app só abre o EXE quando todos os downloads e validações terminarem.

O arquivo catalog.example.json mostra o modelo completo.

## Arquivos locais

O aplicativo salva configurações, cache e pacotes baixados em:

~~~text
%LOCALAPPDATA%\TradutorHub
~~~

Os pacotes ficam separados por tradução e versão. Os arquivos originais dos jogos continuam nos repositórios de cada tradução; o catálogo apenas aponta para as releases oficiais.

## Segurança e limitações

- O aplicativo não baixa executáveis de URLs fora do catalog.json.
- Cada asset pode exigir SHA-256; sem hash, o download é permitido, mas não há validação de integridade.
- A abertura do EXE sempre pede confirmação.
- O Windows pode apresentar SmartScreen ou UAC, especialmente para instaladores sem assinatura digital.
- O usuário deve confirmar que a release e o hash pertencem ao projeto correto.

