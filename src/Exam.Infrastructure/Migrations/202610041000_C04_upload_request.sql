-- Append a targeted upload notification; existing command types retain their wire names.
ALTER TABLE exam.command_events DROP CONSTRAINT command_events_event_type_check;
ALTER TABLE exam.command_events ADD CONSTRAINT command_events_event_type_check
    CHECK (event_type IN ('Start', 'End', 'ScheduleChanged', 'PaperUpdated',
                         'DeviceDecided', 'CollectionCompleted', 'UploadRequested'));
