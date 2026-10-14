"""Reviewed database boundaries from Engine source, without database execution."""
def add_database_views(add):
    def view(id,title,body,note,ops=[]):
        add(id,title,'start\n'+body+'\nstop',['Laplace-Engine/src/db.c'],note,ops)
    view('engine-stage-open','Modular Engine — staging initialization','''
if (Process-static stage_ready already true?) then (yes)
 :Return true; preserve current stage rows;
 stop
endif
:Compare count of staged entity/physicality/consensus tables with public partitions;
if (Counts match?) then (yes)
 :Skip table creation;
else (no)
 :Create stage schema if absent;
 :Create missing unlogged tables from public leaf defaults;
 :Create missing consensus claim indexes;
 if (DDL fails?) then (yes)
  :Return false;
  stop
 endif
endif
:stage_emptied enumerates every ordinary table in stage schema;
:TRUNCATE every stage table found to contain rows;
:Record success in stage_ready; return status;''','This first-use behavior is significant for a fresh standalone merge command: merge calls stage_open before counting pending rows and before resolve_parts. Within an ingest process that already initialized staging, the cached flag skips clearing. The count comparison is not a schema-equivalence check. Source behavior is shown without executing any stage mutation.', [])
    add('engine-read-snapshot','Modular Engine — shared read snapshot','''
participant "Read handler" as Caller
participant "db_read / main.c" as Read
database "Primary connection" as Primary
participant "pool_open / read.c" as Pool
database "Pooled connections" as Workers
Caller -> Read : db_read(conninfo)
Read -> Primary : connect
Read -> Primary : BEGIN REPEATABLE READ, READ ONLY
alt begin fails
 Read --> Caller : exit(1), process terminates
else begin succeeds
 Read -> Primary : SELECT pg_export_snapshot()
 alt export fails or no row
  Read --> Caller : exit(1), process terminates
 else export succeeds
  Read -> Read : retain snapshot ID in process-static buffer
  Read --> Caller : primary connection with transaction open
  Caller -> Pool : a read requires pooled leaf queries
  alt pool already exists
   Pool --> Caller : reuse pool; no new import
  else pool not open
   Pool -> Workers : open max(1, omp_get_max_threads()) connections
   alt snapshot ID exists
    loop every pool connection
     Pool -> Workers : BEGIN REPEATABLE READ, READ ONLY
     Pool -> Workers : SET TRANSACTION SNAPSHOT retained ID
     alt import fails
      Pool --> Caller : exit(1), process terminates
     end
    end
   else no snapshot ID
    note over Pool,Workers : No snapshot import in this branch.
   end
  end
 end
end
note over Caller,Workers
Observed in source: pull, hop, translate, degrees, fills, turn and pgn call db_read.
Scope is this primary transaction and pools importing its snapshot.
Persistent replay evidence is a separate question.
end note
''',['Laplace-Engine/src/main.c','Laplace-Engine/src/read.c'],'The pool imports only when first opened. This diagram does not claim every connection or write path uses the snapshot, nor that a snapshot ID is a durable replay receipt. Connection failures terminate the command. No database connection was opened for this review.',[])
    view('engine-standings-write','Modular Engine — source series to staged standings','''
:fold_events skips known/skipped files and non-claim events;
:Per claim, accumulate games and sum of carried scores;
if (Fold empty?) then (yes)
 :standings_write returns 0;
 stop
endif
:Open connections; request synchronous_commit off; stage_open;
if (Stage setup fails?) then (yes)
 :Return 1;
 stop
endif
:Create stock standings for fold claim IDs;
:Group IDs by first hex digit, in chunks up to 100000;
:Read public standings then staged standings, with staged values overriding;
if (Any read fails?) then (yes)
 :Return 1;
 stop
endif
:In parallel per claim, lp_attest_series(trust, games, mean score);
:Add games to matches;
:Assign 16 consensus partitions across at most 16 writers;
fork
 :Writer begins transaction;
 :Select new or changed-match-count standings;
 :Delete their existing staged rows in chunks;
 :Binary COPY replacement standings into stage;
 if (Partition operation reports failure?) then (yes)
  :Attempt writer ROLLBACK;
 else (no)
  :Attempt writer COMMIT;
 endif
fork again
 :Other assigned writers execute the same sequence independently;
end fork
:Collect reported failures and statistics; close connections;
:Return 1 if any reported failure, otherwise 0;''','Each writer commits independently here; this is not the merge two-phase commit. BEGIN and synchronous_commit SET statuses are discarded. COPY helpers can exit the process directly, bypassing the ordinary return/rollback branch. No lineage grouping occurs inside this fold routine: its key is claim ID and its scope is the caller-provided source fold. This does not establish complete dependence-root handling.', ['13.1','13.2','13.3','13.4'])
    view('engine-merge-transaction','Modular Engine — merge transaction and recovery boundary','''
:Connect; stage_open (first use clears existing stage rows);
:resolve old prepared transaction parts;
if (Setup or recovery fails?) then (yes)
 :Return 1;
 stop
endif
:Read WAL mode and staged leaf catalog;
:Open read/writer connections; request synchronous_commit off;
:Measure staged rows and whether each live leaf holds rows;
if (Checked catalog/count query fails?) then (yes)
 :Return 1;
 stop
endif
:Choose rewrite only for empty non-entity leaves with minimal WAL;
if (Nothing staged?) then (yes)
 :Cleanup; return 0;
 stop
endif
:Sort entities before paths; assign leaf ranges to writers;
:BEGIN each writer; SET LOCAL session_replication_role = replica;
:Read part-zero transaction ID;
if (Checked begin/setup fails?) then (yes)
 :Return 1; no ordinary rollback loop on this branch;
 stop
endif
:Run merge_leaf on assigned leaves in parallel;
if (Leaf operation reports failure?) then (yes)
 :Attempt ROLLBACK on every writer;
 :Return 1; rollback statuses are not checked;
 stop
endif
:PREPARE parts 1 through N-1 in order;
:PREPARE part zero last;
if (Any prepare fails?) then (yes)
 :Return 1; previously prepared parts may remain;
 stop
endif
:COMMIT PREPARED part zero first, then remaining parts;
if (Any commit fails?) then (yes)
 :Return 1; prior committed parts remain; recovery required;
 stop
endif
:ANALYZE rewritten leaves; sample WAL statistics; report rows/timings;
:Cleanup; return 0;''','The protocol has explicit prepare and commit failure boundaries. Source order and recovery logic are shown; crash consistency and concurrency were not exercised. ANALYZE status is discarded. stage_open has its own effects before writer transactions; no whole-source ingest transaction is established by this merge routine.', [])
    view('engine-merge-leaf','Modular Engine — merge one leaf','''
:Resolve live table columns;
if (No columns?) then (yes)
 :Return failure;
 stop
endif
if (Rewrite selected?) then (yes)
 :COPY merged live/staged rows out through read connection;
 :For consensus, staged claim replaces live claim;
 :Buffer binary result in memory;
 if (COPY read succeeds?) then (yes)
  :TRUNCATE live leaf;
  :COPY buffered rows into live leaf with FREEZE;
 endif
else (append/update)
 if (Consensus leaf?) then (yes)
  :UPDATE existing claims from stage;
  :INSERT stage claims absent from live leaf;
 else (entity or path)
  :INSERT staged rows in declared order;
 endif
endif
if (Any reported failure?) then (yes)
 :Return failure;
 stop
endif
if (Physicality leaf?) then (yes)
 :Clean GIN pending lists inside writer transaction;
 :Lock matching entity leaf SHARE ROW EXCLUSIVE;
 :Count staged paths whose entity is absent;
 if (Query/lock fails or missing entities found?) then (yes)
  :Return failure;
  stop
 endif
endif
:TRUNCATE staged leaf if prior steps succeeded;
:Return reported status to merge coordinator;''','Runs within caller-owned writer transaction. Entity existence is checked as a set after referential triggers are disabled by the coordinator. The copy rewrite buffers a whole leaf in memory. Library failures and unchecked allocations are not represented as invented typed receipts.', [])
    view('engine-resolve-parts','Modular Engine — recover prepared transaction parts','''
:Begin two recovery rounds;
while (Another recovery round?) is (yes)
 :Query laplace-prefixed prepared transactions in current database;
 :Only consider parts older than two minutes; order by numeric part;
 if (Query fails?) then (yes)
  :Return false;
  stop
 endif
 while (More returned parts?) is (yes)
  if (First round and part is not zero?) then (yes)
   :Skip until next round;
  else (eligible)
   if (Part zero or coordinator status committed?) then (yes)
    :COMMIT PREPARED;
   elseif (Coordinator status aborted?) then (yes)
    :ROLLBACK PREPARED;
   else (other status)
    :Leave part unresolved for now;
   endif
   if (Issued statement fails?) then (yes)
    :Return false;
    stop
   endif
  endif
 endwhile (no)
endwhile (no)
:Return true;
''','Part zero is prepared last and committed first by load/merge. Recovery commits a prepared part zero, then follows its transaction status for other parts. The two-minute eligibility threshold is literal source policy. Runtime crash-recovery behavior is not tested by this atlas.', [])
