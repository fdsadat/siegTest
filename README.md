# Fiscal Documents API

- Não sei se vou apresentar o projeto, então passei pela maioria dos arquivos deixando comentários com o que pensei durante a implementação. Sei que não é boa prática deixar tantos comentários, mas como se trata de um teste, acho que pode ajudar na correção.
- Não criei branches, nem comentários nos commits padronizados. Mas em um ambiente profissional, criaria no padrão definido.

## Como rodar a aplicação e os testes? (Sem Docker)

- No Windows: SDK .NET 10 e Windows PowerShell 5.1 ou PowerShell 7.
- SQL Server Express LocalDB, disponível no Visual Studio Installer, ou outra instância SQL Server.
- RabbitMQ 4.x com Erlang compatível e serviço em execução.

Na raiz do repositório, execute:

```powershell
dotnet restore FiscalDocuments.sln
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Initialize-Local.ps1
```

-O script gera/preserva os segredos em `.env`, configura API e worker, verifica as conexões e aplica as migrations. Não instala programas. O padrão é SQL `(localdb)\MSSQLLocalDB`, banco `FiscalDocuments`, e 
-RabbitMQ `localhost:5672`, usuário/senha `guest`/`guest`, vhost `/`.

Inicie API e worker em dois terminais separados, ou rode no VS os 2 ao mesmo tempo para debugar:

```powershell
dotnet run --project src/FiscalDocuments.Api --launch-profile "Local Debug"
dotnet run --project src/FiscalDocuments.Worker --launch-profile "Local Debug"
```

- Swagger: http://localhost:54392/swagger. 
- Criei um `POST /api/v1/auth/token` para pegar um Bearer, mas só por questões de teste mesmo.
- Criei uma pasta SAMPLES com alguns arquivos de teste, se quiser usar ou saber oq usei para os testes.

O upload aceita até dez arquivos de 2 MiB.

Execute os testes na raiz; API e worker não precisam estar em execução:

```powershell
dotnet test FiscalDocuments.sln
dotnet test tests/FiscalDocuments.UnitTests
dotnet test tests/FiscalDocuments.UnitTests --filter "TestCategory=Architecture"
```

Se quiser rodar o Reconcile para ler a tabela DLQ

```powershell
dotnet run --project src/FiscalDocuments.Worker --launch-profile "Local Debug" -- --reconcile-dlq
```

NUnit cobre regras unitárias, HTTP, integração SQL/RabbitMQ, idempotência, concorrência, retries e DLQ. Os testes de arquitetura verificam referências entre camadas e o isolamento de Domain/Application.
Os testes de integração usam LocalDB e RabbitMQ reais com os mesmos padrões locais, criando bancos e filas exclusivos de teste.

Para testar os retrys, a DLQ e o endpoint /reprocess, eu rodei no BD uma constraint q n faz sentido

ALTER TABLE dbo.DocumentSummaries WITH NOCHECK
ADD CONSTRAINT CK_ManualRetry_BlockSummary
CHECK (TotalAmountInCents < 0);

Depois chamei o upload de um arquivo, vai ficar dando erro, ai tem como ver as filas de retry sendo chamandas e a DLQ no final. Depois é só voltar o BD ao normal.

ALTER TABLE dbo.DocumentSummaries
DROP CONSTRAINT CK_ManualRetry_BlockSummary;

## Quais foram as decisões de arquitetura e modelagem?

Usei Clean Architecture com DDD e MediatR para o CQRS.

**Clean Architecture:** Domain contém as entidades e regras de negócio; Application reúne casos de uso e interfaces; Infrastructure implementa persistência, XML e mensageria; Api e Worker são os pontos de entrada. As dependências apontam para as camadas internas.
**Domain Driven Design (DDD):** `FiscalDocument` e `DocumentProcessing` concentram invariantes, como identidade fiscal imutável e transições de processamento. 
**CQRS com MediatR:** comandos alteram dados por handlers e consultas usam projeções com `AsNoTracking` e paginação por cursor. Ambos utilizam o mesmo banco, sem duplicar a persistência.
**Por que SQL Server e não MongoDB?** Escolhi SQL Server pela minha experiência pessoal com a tecnologia e porque o problema favorece relacionamentos, índices únicos, transações e controle de concorrência com `rowversion`. EF Core facilita o mapeamento e as migrations. Acredito que tudo que fiz no SQL Server possa ser feito com MongoDB, então, acima de tudo foi uma escolha por experiência pessoal mesmo.

Resumo da modelagem do BD:
| Tabela | Comentário
| --- | --- |
| `Documents`             | Tabela principal
| `DocumentProcessing`    | Criei uma maquina de estados, qnd recebe o arquivo salva e fica 'PendingExtraction', depois q passa pelo worker 'Complete'
| `DocumentSummaries`     | Aqui é a tabela de resumo, que é o processamento final, depois q consome a mensagem.
| `UploadReceipts`        | Idempotencia para a lista de documentos. Salva o fingerprint de cada documento.
| `OutboxMessages`        | Tabela pro Outbox clássica, a API salva o upload e um registro nessa tabela, pra depois o Worker ir lá e criar as mensagens.
| `InboxMessages`         | Essa tabela é para que quando processado, antes de mandar o ACK pro RabbitMQ, salve q foi processado, pq se o ACK não chegar, fica o registro.
| `IdempotentOperations`  | Fingerprint e resposta das operações HTTP idempotentes, com retenção de 24 horas.
| `ProcessingFailures`    | Registro de falhas definitivas por execução para diagnóstico.

### Como funciona a mensageria e quais classes participam?

1- A API grava documento, atualiza status de processamento, salva o recibo e salva no Outbox na mesma transação SQL. 
2- OutboxDispatcher lê a tabela de Outbox e publica no RabbitMQ.
3- RabbitConsumer lê a fila com o auxilio do RabbitBroker que gerencia o acesso ao RabbitMQ e processa os dados com auxilio do EventProcessor que faz a transição final das chamadas.
4- O endpoint /reprocess reinicia o processamento de um documento em Failed, criando uma nova execução e um novo evento na outbox. A mensagem antiga permanece na DLQ.
5- O DlqReconciler lê manualmente as mensagens da DLQ, registra a falha no banco quando aplicável e confirma a retirada da mensagem. Mensagens de execuções antigas, documentos excluídos ou já concluídos são retiradas sem alterar o estado atual.

Falhas usam três filas de retry com esperas de 5, 30 e 120 segundos e uma DLQ (fila de falhas). 

- Índices únicos de identidade/recibos evitam duplicidade; 
- outbox/inbox toleram reentregas; 
- Existe lógica para preservar a proteção contra reinserção por reenvio. 
- O ETag impede sobrescrever alterações concorrentes.

## Como foram tratados os dados sensíveis?

- O upload é lido em streaming, sem gravar o XML original em arquivos temporários. Mantive somente a projeção sanitizada no banco, com emitente(CPF/CNPJ) mascarado e sem destinatários, textos livres, assinaturas ou certificados.
- `Fingerprints` é uma classe definida para usar HMAC-SHA-256 com chave secreta para fazer comparações sem guardar os originais. Não é reversível, mas consigo comparar usando a chave.
- Usei JWT com 3 permissões(read, write, replay) para limitar o acesso dos usuarios e coloquei limites de requisições na API; 
- Mensagens e logs não incluem o XML original.

## Quais seriam as possíveis melhorias com mais tempo?

- Criaria um teste de carga com NBomber para medir latências, vazão e erros das consultas.
- Separaria extração e criação do resumo em duas mensagens para escalar e tratar falhas de cada etapa independentemente, caso o volume ou a complexidade crescessem.
- Revisitaria o endpoint de envio de XMLs para aprofundar as validações de dados e ampliar os layouts suportados, considerando XSDs e regras fiscais conforme a necessidade. Hoje a validação cobre a estrutura e os campos usados, sem certificar validade fiscal ou assinatura.
- Coloquei os segredos em um .env. Faria um sistema mais robusto, usando algo na cloud talvez, para manter os segredos.
- Adicionaria mais logs e teria algum tipo de monitoramento. Só coloquei logs de falhas.
