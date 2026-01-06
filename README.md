```mermaid
flowchart TD
    %% ==========================================
    %% 1. INITIALIZATION PHASE
    %% ==========================================
    subgraph Init [Phase 1: Initialization]
        Start((HTTP Start)) -->|POST /start| Orch_Start[Start OnCallOrchestrator]
        Orch_Start --> Act_Config[Activity: GetConfig]
        Act_Config --> Ent_Init[Entity: Initialize State]
    end

    %% ==========================================
    %% 2. ORCHESTRATOR LOGIC (MAIN WORKFLOW)
    %% ==========================================
    subgraph Orchestrator [Phase 2: Orchestrator Logic]
        direction TB
        
        %% --- STEP LOOP ---
        Ent_Init --> Step_Loop_Start{Loop: For Each Step}
        Step_Loop_Start -->|Next Step| Step_Setup[Log Start Step & Reset Step Memory]
        
        %% --- REPLENISHMENT LOOP (Batching) ---
        Step_Setup --> Batch_Loop_Start{Loop: Replenishment}
        
        Batch_Loop_Start -->|Start Round| Check_Mission_1{Is Mission Complete?}
        Check_Mission_1 -- Yes --> Mission_Success
        Check_Mission_1 -- No --> Ent_GetBatch[Entity: GetBatchUsers]
        
        Ent_GetBatch --> Check_Batch{Users Found?}
        Check_Batch -- No (Empty) --> Step_Finished[Log: Step Finished]
        Step_Finished --> Step_Loop_Start
        
        Check_Batch -- Yes (Found Users) --> Action_Loop_Start
        
        %% --- ACTION & RETRY LOOP ---
        subgraph Action_Process [Action Processing]
            Action_Loop_Start{Loop: For Each Action}
            Action_Loop_Start --> Retry_Loop_Start{Loop: Repeat/Retry}
            
            Retry_Loop_Start --> Check_Mission_2{Is Mission Complete?}
            Check_Mission_2 -- Yes --> Mission_Success
            
            Check_Mission_2 -- No --> Ent_Filter[Entity: FilterPendingUsers]
            Ent_Filter --> Check_Pending{Any Pending Users?}
            
            Check_Pending -- No (All Responded) --> Batch_Loop_Start
            Check_Pending -- Yes --> Do_Action[Activity: Call or SMS]
            
            Do_Action --> Timer_Wait[Timer: Wait X Minutes]
            Timer_Wait --> Retry_Loop_Start
        end

        %% Connections out of loops
        Retry_Loop_Start -- End Retries --> Action_Loop_Start
        Action_Loop_Start -- End Actions --> Batch_Loop_Start
    end

    %% ==========================================
    %% 3. WEBHOOK (USER INTERACTION)
    %% ==========================================
    subgraph Webhook_Flow [Phase 3: Asynchronous Webhook]
        User((User Action)) -->|Click Accept/Decline| API_Webhook[HTTP: HandleOnCallResponse]
        API_Webhook -->|Start Sub-Orch| Mediator[Mediator Orchestrator]
        Mediator -->|Call| Ent_Update[Entity: UserAccepted / UserDeclined]
        Ent_Update -->|Return Result| Mediator
        Mediator -->|Response| API_Webhook
    end

    %% ==========================================
    %% 4. ENTITY (STATE MANAGEMENT)
    %% ==========================================
    subgraph Entity_State [OnCallEntity State]
        Ent_DB[(Entity State)]
        
        %% Relationships to Entity
        Ent_Init -.-> Ent_DB
        Ent_GetBatch -.->|Read/Write ActiveBatch| Ent_DB
        Ent_Filter -.->|Read Status| Ent_DB
        Ent_Update -.->|Update Accepted/Declined| Ent_DB
        Check_Mission_1 -.->|Read Quota| Ent_DB
        Check_Mission_2 -.->|Read Quota| Ent_DB
    end

    %% ==========================================
    %% 5. FINISH
    %% ==========================================
    subgraph Finish [Phase 4: Completion]
        Mission_Success((Mission Complete)) --> Report[Generate Report]
        Step_Loop_Start -- No Steps Left --> Report
        Report --> Save_DB[Activity: Save Summary]
        Save_DB --> Ent_Delete[Entity: Delete State]
        Ent_Delete --> End_Process((End Workflow))
    end
