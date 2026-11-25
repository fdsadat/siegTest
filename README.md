# Fiscal Documents API

API do desafio técnico em .NET 10, Clean Architecture, DDD e CQRS com MediatR. SQL Server garante unicidade e concorrência; RabbitMQ transporta eventos com entrega *at least once*. Outbox e inbox tornam os efeitos de negócio idempotentes.

## Executar

Pré-requisitos: Docker Engine/Desktop com containers Linux e Compose. Para compilar e testar fora dos containers: SDK .NET 10. Os scripts funcionam no Windows PowerShell 5.1 e no PowerShell 7 (também disponível em Linux). Execute os comandos abaixo em uma dessas sessões; não é necessário ter `pwsh` instalado no Windows.

```powershell
& ./scripts/New-DevEnvironment.ps1
docker compose up --build -d
docker compose logs -f api worker
```

Se o Windows PowerShell bloquear a execução de scripts, use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/New-DevEnvironment.ps1`. Para o token, use `$token = powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/New-DevToken.ps1`. A opção vale somente para o processo iniciado, sem alterar a política permanente do Windows.

O script cria um `.env` ignorado pelo Git com segredos independentes. Reexecutá-lo preserva os valores existentes. Preserve esse arquivo entre reinicializações: a chave HMAC é necessária para reconhecer documentos já recebidos. A troca da chave exige uma estratégia de migração dos identificadores, fora deste desafio.

- API/Swagger: http://localhost:8080/swagger
- Liveness: http://localhost:8080/health/live
- Readiness SQL/migrations: http://localhost:8080/health/ready
- Administração RabbitMQ: http://localhost:15672, usuário `fiscal_app`, senha gerada em `.env`.

O worker conecta ao RabbitMQ em `rabbit:5672` pela rede interna do Compose; essa porta não é publicada no host, evitando conflitos com instalações locais do RabbitMQ.

O serviço `migrate` aplica migrations e cria o usuário SQL `fiscal_app`, com leitura/escrita de dados. API e worker não usam a conta `sa`. O Compose mantém volumes de SQL e RabbitMQ e expõe portas apenas em loopback. A demonstração usa um nó RabbitMQ; isso não constitui alta disponibilidade contra perda do nó/volume.

O health check do SQL permite criar o banco na primeira execução e, quando `FiscalDocuments` já existe, espera que ele esteja acessível antes de iniciar migrations. Isso evita tentar recriar o banco enquanto ele ainda está em recuperação após uma reinicialização.

Para gerar um token com validade de duas horas, execute **POST /api/v1/auth/token** no Swagger, sem autenticação e sem corpo. Copie `accessToken`, clique em **Authorize** e cole o token.

**Esse endpoint não deveria existir em produção: foi incluído apenas para facilitar os testes deste projeto.** Ele é registrado somente em `Development`, quando `Jwt:Authority` não está configurado, e concede as três permissões de teste.

```powershell
$auth = Invoke-RestMethod -Method Post http://localhost:8080/api/v1/auth/token
$token = $auth.accessToken
$headers = @{ Authorization = "$($auth.tokenType) $token" }
```

Como alternativa, o script local continua disponível:

```powershell
$token = & ./scripts/New-DevToken.ps1
$headers = @{ Authorization = "Bearer $token" }
```

Swagger possui botão **Authorize** para colar o token. JWT valida assinatura, emissor, público e expiração. As permissões são `documents.read`, `documents.write` e `operations.replay`. Em um ambiente integrado, configure `Jwt__Authority` com um provedor HTTPS e `Jwt__Audience`; não há servidor de identidade implementado neste projeto.

## Depurar API e worker no Visual Studio

Execute SQL Server, RabbitMQ e migrations em Docker; execute API e worker locais com o debugger:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Set-LocalDebugEnvironment.ps1
docker compose stop api worker
docker compose -f compose.yaml -f compose.debug.yaml up -d sql rabbit migrate
```

O script reutiliza os segredos de `.env` e gera `appsettings.Development.json` nos dois projetos, preservando configurações não relacionadas. Esses arquivos são ignorados pelo Git e excluídos do contexto Docker. O override publica AMQP em `localhost:5673`, pois `5672` pode estar ocupado por uma instalação local. SQL permanece em `localhost:1433`.

Abra `FiscalDocuments.sln`, configure múltiplos projetos de inicialização e escolha **Start** para `FiscalDocuments.Api` e `FiscalDocuments.Worker` (demais: **None**). Selecione o perfil **Local Debug** para ambos e pressione **F5**. Swagger local: http://localhost:54392/swagger. Gere o token pelo endpoint `POST /api/v1/auth/token` ou pelo script, como acima.

Breakpoints úteis: `DocumentsController.Upload`, `OutboxDispatcher.DispatchOneAsync`, `RabbitConsumers.ConsumeAsync` e `EventProcessor.ProcessAsync`. Envie um XML novo para percorrer todo o fluxo; repetir conteúdo já recebido não inicia outro processamento. Mantenha o worker Docker parado durante a depuração para que ele não consuma as mensagens antes do worker local. Pausas longas podem expirar leases ou conexões e provocar retries/reentregas.

Para executar sem debugger em dois terminais, use `dotnet run --project src/FiscalDocuments.Api --launch-profile "Local Debug"` e `dotnet run --project src/FiscalDocuments.Worker --launch-profile "Local Debug"`. Para voltar ao modo Docker, encerre os processos locais e execute `docker compose up -d`.

## Fluxo e organização

```mermaid
flowchart LR
  API[API / MediatR] -->|transação por arquivo| SQL[(Documento + recibo + outbox)]
  SQL --> Dispatcher[Publicador do outbox]
  Dispatcher --> Received[RabbitMQ: DocumentoRecebido]
  Received --> Extractor[Consumidor de extração]
  Extractor -->|resultado + inbox + outbox| SQL
  Dispatcher --> Processed[RabbitMQ: DocumentoProcessado]
  Processed --> Summary[Consumidor de resumo]
  Summary -->|resumo + inbox + Completed| SQL
```

| Projeto | Responsabilidade |
| --- | --- |
| Domain | Agregado fiscal, identidade imutável e transições do processamento; sem dependências externas. |
| Application | Comandos/consultas MediatR, validação, casos de uso, contratos e portas de persistência. |
| Infrastructure | EF Core/SQL Server, parsers, HMAC, RabbitMQ, outbox e inbox. |
| Api | HTTP, JWT, autorização, limites, tratamento de erros e Swagger. |
| Worker | Um host para publicador e os dois consumidores; comando operacional de reconciliação. |

CQRS usa o mesmo banco: comandos passam pelos agregados e pela unidade de trabalho; consultas usam projeções EF Core `AsNoTracking` e paginação por cursor. Não há um segundo banco de leitura nem histórico dos XMLs.

Estados: `PendingExtraction → PendingSummary → Completed`. Uma falha permanente ou o esgotamento dos retries leva a `Failed`. Falhas transitórias continuam pendentes. `Completed` só ocorre após o resumo persistido pelo segundo consumidor.

## Contratos HTTP

| Método/rota | Comportamento |
| --- | --- |
| `POST /api/v1/auth/token` | Somente testes em Development; gera Bearer de duas horas sem autenticação ou corpo. |
| `POST /api/v1/documents` | Multipart com até 10 arquivos de 2 MiB; resultado individual por arquivo. |
| `GET /api/v1/documents` | Filtros e paginação, sem conteúdo XML na listagem. |
| `GET /api/v1/documents/{id}` | Documento atual, XML sanitizado, processamento, resumo e cabeçalho `ETag`. |
| `PUT /api/v1/documents/{id}` | Um XML no corpo `application/xml`, mantendo tipo e identidade fiscal. |
| `DELETE /api/v1/documents/{id}` | Exclusão lógica; repetição retorna `204`. |
| `POST /api/v1/documents/{id}/reprocess` | Reinicia somente uma execução atual em `Failed`. |

Upload de exemplo, incluindo arquivo inválido e duplicado:

No Windows PowerShell 5.1, use `curl.exe` no lugar de `curl` para chamar o executável, pois `curl` é um alias de `Invoke-WebRequest`.

```powershell
curl -X POST http://localhost:8080/api/v1/documents `
  -H "Authorization: Bearer $token" `
  -F "files=@samples/nfe.xml" -F "files=@samples/cte.xml" `
  -F "files=@samples/nfse.xml" -F "files=@samples/invalid.xml" `
  -F "files=@samples/nfe.xml"
```

O HTTP externo é `200`, com `results[]`: `inputIndex`, `statusCode`, `document` ou `errorCode`. Um item novo tem `202`; um conteúdo conhecido tem `200` e `disposition=AlreadyReceived`. A resposta inclui a revisão/situação **atuais** e `isCurrentContent`, que distingue um recebimento antigo cujo XML já foi sobrescrito.

Cada aceitação tem sua própria transação. Se a conexão cair, houver erro de infraestrutura ou o multipart for interrompido, aceitações anteriores continuam válidas, mesmo que a resposta completa não chegue. Reenvie o lote inteiro para recuperar os resultados. Um lote com mais de dez partes devolve os dez resultados e um item indicando que os demais não foram aceitos. Limites globais ou multipart inválido podem encerrar a requisição com erro.

Filtros: `from` inclusivo, `to` exclusivo, `issuerCnpj` exato (com ou sem pontuação), `state` em maiúsculas, `type` (`NFe`, `CTe`, `NFSe`), `status`, `pageSize` (1–100; padrão 20) e `cursor`. O filtro de identificador também aceita CPF de emitente NFSe. Datas são consultadas como instantes UTC; a competência preserva o mês local informado no XML. Ordenação por emissão e ID, ambos decrescentes. Reutilize `nextCursor` mantendo os filtros.

```powershell
Invoke-RestMethod 'http://localhost:8080/api/v1/documents?state=SP&pageSize=20' -Headers $headers
```

### Substituição, concorrência e idempotência

PUT e DELETE exigem `If-Match` com o ETag entre aspas. Ausência: `428`; formato inválido: `400`; valor desatualizado: `412`. `rowversion` é o token atual de concorrência, não um histórico. O worker altera registros de processamento separados e não invalida esse ETag.

PUT, DELETE e reprocessamento exigem `Idempotency-Key` de 8 a 128 caracteres ASCII. A operação é identificada por usuário, ação, documento e chave; o fingerprint também considera o corpo e as precondições aplicáveis. A resposta bem-sucedida é persistida na mesma transação. Por 24 horas, uma repetição idêntica recebe a resposta original, inclusive após mudança do ETag; outra operação com a mesma chave retorna `409`. Registros expirados podem ser reutilizados. O índice de identidade e os recibos de conteúdo não expiram.

Para substituir, espere `Completed` ou `Failed`. Enquanto houver extração, resumo ou retry pendente, a resposta será `409`. Mesma identidade com conteúdo diferente no POST também retorna `409`: use PUT. Conteúdo igual ao atual é uma operação sem alteração; conteúdo anterior conhecido retorna `409`, sem restauração.

```powershell
$id = 'ID-RETORNADO-PELO-UPLOAD'
$details = Invoke-RestMethod "http://localhost:8080/api/v1/documents/$id" -Headers $headers
$mutationHeaders = @{
  Authorization = "Bearer $token"
  'If-Match' = $details.document.eTag
  'Idempotency-Key' = [guid]::NewGuid().ToString()
}
$newXml = (Get-Content -Raw ./samples/nfe.xml).Replace('150.25', '175.00')
Invoke-RestMethod "http://localhost:8080/api/v1/documents/$id" -Method Put `
  -Headers $mutationHeaders -ContentType 'application/xml' -Body ([Text.Encoding]::UTF8.GetBytes($newXml))
```

O PUT aceito retorna `202` e `Location`; consulte esse endereço para obter o novo ETag. A transação sobrescreve o XML, incrementa `ContentRevision`, gera outro `ProcessingId`, limpa o resumo e cria o evento. O conteúdo anterior é irrecuperável. Recibos preservam apenas fingerprints e metadados de recebimento, sem XMLs antigos.

Exclusão oculta o documento, mantendo tombstone e recibos contra reinserção. Mensagens em trânsito não o recriam. O replay mantém a revisão do conteúdo, troca a execução e reinicia o fluxo completo a partir do XML sanitizado atual.

## Formatos e dados sensíveis

Os arquivos de `samples/` são exemplos sintéticos reduzidos para o contrato implementado; não são notas autorizadas nem exemplos completos válidos contra todos os XSDs oficiais.

| Tipo | Raiz/layout | Identidade | Emissão / emitente / UF / valor |
| --- | --- | --- | --- |
| NF-e | `NFe` ou `nfeProc`, namespace Portal Fiscal, 4.00, modelo 55 | `infNFe/@Id` | `ide/dhEmi`, `emit/CNPJ`, `emit/enderEmit/UF`, `total/ICMSTot/vNF` |
| CT-e | `CTe` ou `cteProc`, namespace Portal Fiscal, 4.00, modelo 57 | `infCte/@Id` | `ide/dhEmi`, `emit/CNPJ`, `emit/enderEmit/UF`, `vPrest/vTPrest` |
| NFSe | `NFSe`, namespace SPED NFSe, nacional 1.01 | `infNFSe/@Id` | `DPS/infDPS/dhEmi`, `prest/CPF` ou `CNPJ`, UF pelo prefixo IBGE de `prest/end/endNac/cMun`, `valores/vServPrest/vServ` |

O segundo consumidor calcula `competence` (`AAAA-MM`) e `totalAmountInCents` com decimal, sem ponto flutuante. DTD, entidades externas, campos ambíguos, profundidade acima de 64, tamanhos excessivos e valores monetários inválidos são rejeitados. Não há consulta fiscal externa, validação XSD, cálculo de dígito verificador ou validação de assinatura.

Antes da persistência, o parser cria uma projeção XML interna (`FiscalDocument schema=1`) com tipo, emitente totalmente mascarado, emissão, UF e total. Texto livre, destinatários, chaves completas, assinatura/certificados e campos não utilizados são descartados. Esse XML interno tem contrato próprio para reprocessamento e não representa o arquivo fiscal original assinado.

HMAC-SHA-256 é calculado em memória, antes da máscara, para identidade fiscal, identificador do emitente e bytes do conteúdo. Isso protege os identificadores de uma comparação direta com hashes públicos. A garantia por conteúdo é de igualdade de bytes: diferenças de encoding, envelope ou formatação podem gerar fingerprints distintos; a identidade fiscal única continua impedindo outro documento.

Uploads são lidos por `MultipartReader`, sem buffering de arquivos originais em disco; há limite de quatro uploads concorrentes e 120 requisições/minuto por usuário/IP. Respostas de erro usam códigos e trace ID; logs de acesso detalhados e exceções com payload não são habilitados. Em produção, utilize TLS no ingresso, restrinja acesso ao Swagger/administração e configure o proxy para não registrar CNPJ/CPF da query string. Segredos devem ser fornecidos por um gerenciador apropriado, e não pelo `.env` de demonstração.

## Confiabilidade e reconciliação

- Outbox reserva uma mensagem por lease de dois minutos; falha de publicação deixa a linha pendente com backoff exponencial limitado a cinco minutos. Lease expirado permite recuperação após queda.
- Publicação persistente, `mandatory` e publisher confirms. Somente confirmação válida permite marcar `PublishedAt`.
- Consumidores usam ACK manual após commit SQL. A combinação inbox + unicidade do resultado impede repetir efeitos após queda entre commit e ACK.
- Em falha transitória, três retries passam por filas com TTL de 5, 30 e 120 segundos. A publicação na fila de retry é confirmada antes de ACK da mensagem original.
- Falha definitiva é registrada em SQL e encaminhada à DLQ com confirmação. Se SQL, confirmação ou transferência falhar, não há ACK de sucesso; a entrega volta ao broker.
- Filas quorum duráveis têm limite e dead lettering `at-least-once` com `reject-publish`. A DLQ também recebe mensagens que excedam o limite de entregas do broker.
- Cada commit de consumidor, substituição, exclusão e replay usa a mesma coordenação transacional por documento (`sp_getapplock`). Transformação do XML ocorre antes dessa transação. Revisão, execução e exclusão são conferidas novamente antes de gravar.
- As mensagens contêm apenas IDs, revisão, execução, tipo, data e correlação. Reentrega é esperada; não há promessa de entrega exatamente uma vez no broker.

Após investigar/corrigir a causa, reconcilie a DLQ manualmente:

```powershell
docker compose run --rm worker --reconcile-dlq
```

O comando lê até 100 entregas por DLQ, registra a falha atual em SQL e só então confirma a entrega. Mensagens de execuções antigas, documentos excluídos ou já concluídos não alteram o estado atual. Envelope inválido fica retido para inspeção segura pelo operador. A falha permanece em `ProcessingFailures` para diagnóstico.

Então reprocesse a execução atual:

```powershell
$replayHeaders = @{ Authorization = "Bearer $token"; 'Idempotency-Key' = [guid]::NewGuid().ToString() }
Invoke-RestMethod "http://localhost:8080/api/v1/documents/$id/reprocess" -Method Post -Headers $replayHeaders
```

Uma falha durante commit SQL pode ter resultado desconhecido. A unidade de trabalho executa a transação completa pela estratégia de resiliência EF Core; recebimentos consultam identidade/recibos, comandos HTTP consultam a operação idempotente, e consumidores consultam inbox/execução antes de repetir os efeitos.

## Compilação e testes

```powershell
dotnet restore FiscalDocuments.sln
dotnet build FiscalDocuments.sln --no-restore
dotnet test FiscalDocuments.sln --no-build
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/FiscalDocuments.Infrastructure --no-build
```

Os testes de integração criam SQL Server e RabbitMQ reais com Testcontainers; Docker é necessário. Não há substituição por EF InMemory/SQLite, pois os testes precisam verificar `rowversion`, índices, transações, leases e mensagens reais. A CI executa a suíte em Linux com Docker e publica TRX.

Para executar a integração HTTP/SQL em um SQL Server existente ou LocalDB, sem RabbitMQ:

```powershell
$env:FISCAL_TEST_SQL_CONNECTION = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/FiscalDocuments.IntegrationTests --filter 'TestCategory=Sql'
```

A fixture cria e remove apenas um banco exclusivo `FiscalTests_<guid>`; a conta deve poder criar bancos de teste. Os testes `Rabbit` continuam exigindo Docker. Para somente unitários: `dotnet test tests/FiscalDocuments.UnitTests`.

Cobertura principal: três layouts, sanitização, XXE, duplicidade simultânea/lote, rollback, substituição condicionada, ETag, chave idempotente, reenvio antigo, exclusão com mensagem em trânsito, filtros/cursor, JWT/Swagger, retry real, publicação sem rota, redelivery após commit e reconciliação da DLQ.

Dependências ficam fixadas nos projetos. MediatR 12.5.0 foi escolhido sob sua licença Apache-2.0; assertions usam NUnit. A ferramenta EF local está fixada em 10.0.15. Consulte as licenças dos respectivos pacotes antes de atualizar versões principais.
