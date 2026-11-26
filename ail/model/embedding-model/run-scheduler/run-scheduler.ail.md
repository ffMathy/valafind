# A search never waits for indexing

Indexing and searching share one model. Every model run goes through a scheduler that gives searches priority.

```ail
node RunScheduler : fn   level: specialist
  why   The launcher must answer within a fraction of a second even while indexing is busy; a document batch can take
        seconds on the CPU.   <- EmbeddingModel
  what
    two kinds of run: interactive (embedQuery, embedDocumentsInteractive) and background (document batches, every
      vision or audio encoder run, and the text-model run of a picture, video or clip)
    RunScheduler(maxBackgroundRuns: integer = no limit) -> RunScheduler — a cap below 1 counts as 1
    RunScheduler.enterInteractive() -> nothing — marks every running background run preempted and asks ONNX Runtime to
      terminate it, then waits until no background run and no other interactive run is inside, and enters alone
    RunScheduler.enterBackground(run: ActiveRun) -> nothing — waits while an interactive run is inside or waiting, and
      while maxBackgroundRuns are running; background runs may overlap each other
    RunScheduler.enterExclusive() -> nothing — like enterInteractive without terminating anything (closing the model or
      its encoders)
    RunScheduler.exit(run: ActiveRun or nothing) -> nothing — wakes every waiter
    type ActiveRun = { terminate: boolean, preempted: boolean }
    a background run that ONNX Runtime stopped because it was preempted is run again from the start once the
      interactive run has left, and its caller gets the same vectors (cosine above 0.99999), only later
    a run stopped because its own cancellation was asked for ends with "cancelled"; the model stays usable
    every wait can be cancelled and never deadlocks when a cancellation races a wake-up
    examples
      one background run inside, enterInteractive => that run gets terminate true and preempted true, and the
        interactive entry waits until it has left
      two background runs inside, enterInteractive => both are terminated; a third enterBackground waits until the
        interactive run has left
      maxBackgroundRuns 2 with 2 inside, a third enterBackground => still waiting after 100 ms; it enters once one leaves
      a query during a batch of eight ~400-token documents => answered within max(1.5 s, 3 × an idle query), and
        the documents come out as when embedded alone
    the indexer also waits before each batch while a search is in progress, so a batch rarely starts just to be
      stopped
  how
    decide terminate and re-run instead of waiting for the batch to end
      because waiting would cost the query most of a batch, seconds on the CPU (PR #1, "Searches preempt in-flight
      indexing batches")
    decide one encoder run per video frame
      because a query then loses at most one frame's work (PR #9)
    decide document runs overlap, up to what the indexer starts
      because ONNX Runtime's Run is safe across threads and the CPU budget decides how many files run at once (PR #14)
  verify
    property: while an interactive run is inside, no background run is inside
    property: the vectors of a preempted and re-run batch equal those of the same batch run alone
  sources
    PR #1, PR #9, PR #14 (index files in parallel)
    src/Valafind.Core/Embedding/EmbeddingGemmaModel.cs, Scheduled, RunScheduler, ActiveRun; IInteractiveEmbedding.cs
    tests/Valafind.Core.Tests/Embedding/RunSchedulerTests.cs; EmbeddingGemmaModelTests.cs,
      Query_preempts_a_running_document_batch_which_is_then_rerun_correctly,
      Interactive_document_embedding_preempts_a_running_document_batch, Cancelling_a_document_batch_while_it_runs_aborts_it
    tests/Valafind.Core.Tests/Embedding/MediaEmbeddingTests.cs, Query_preempts_a_running_picture_embedding
```
