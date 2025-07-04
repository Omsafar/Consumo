# CamionReportGPT

Applicazione WPF per interrogare dati di consumo camion tramite GPT.

## Prerequisiti
- .NET 8 SDK
- SQL Server raggiungibile con la stringa di connessione indicata in `MainWindow.xaml.cs`.
- Python 3.12 installato e accessibile al percorso specificato in `EseguiPythonAsync`.

## Assistant OpenAI
L'app utilizza due assistant:
- `asst_JMGFXRnQZv4mz4cOim6lDnXe` per generare spiegazioni ed embedding.
- `asst_ANQJ6yo179ZJXEtCZ3xuymSJ` (`GPT_CORRETTORE_ERRORE`) per correggere le risposte che causano errori SQL o Python.

## Correzione automatica
Quando l'esecuzione di una query SQL o di uno script Python genera un errore, la risposta di GPT viene inviata una sola volta all'assistant correttore insieme alla domanda, al prompt iniziale e al messaggio di errore. L'assistant restituisce una nuova risposta che viene eseguita nuovamente. Se anche questo tentativo fallisce, l'errore viene mostrato all'utente.

Per i log consultare `error.log` nella cartella dell'applicazione.