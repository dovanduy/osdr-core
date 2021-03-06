﻿using Automatonymous;
using MassTransit.MongoDbIntegration.Saga;
using Sds.Imaging.Domain.Events;
using Sds.MetadataStorage.Domain.Commands;
using Sds.MetadataStorage.Domain.Events;
using Sds.Osdr.Generic.Domain;
using Sds.Osdr.Generic.Domain.Commands.Files;
using Sds.Osdr.Generic.Domain.Events.Files;
using Sds.Osdr.Generic.Domain.ValueObjects;
using Sds.Osdr.Reactions.Sagas.Commands;
using Sds.Osdr.Reactions.Sagas.Events;
using Sds.Osdr.RecordsFile.Domain.Commands.Files;
using Sds.Osdr.RecordsFile.Domain.Events.Files;
using Sds.ReactionFileParser.Domain.Commands;
using Sds.ReactionFileParser.Domain.Events;
using System;
using System.Collections.Generic;

namespace Sds.Osdr.Reactions.Sagas
{
    public class ReactionFileProcessingState : SagaStateMachineInstance, IVersionedSaga
    {
        public Guid FileId { get; set; }
        public Guid UserId { get; set; }
        public Guid BlobId { get; set; }
        public string Bucket { get; set; }
        public string CurrentState { get; set; }
        public Guid CorrelationId { get; set; }
        public int Version { get; set; }
        public Guid _id { get; set; }
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset Updated { get; set; }
        public long TotalParsedRecords { get; set; }
        public IEnumerable<string> Fields { get; set; }
        public Imaging.Domain.Models.Image Image { get; set; }
        public long ProcessedRecords { get; set; }
        public long FailedRecords { get; set; }
        public FileStatus FileStatus { get; set; }
        public int AllProcessed { get; set; }
		public int AllParsingStepsDone { get; set; }
		public int AllPersisted { get; set; }
    }

    public class ReactionFileProcessingStateMachine : MassTransitStateMachine<ReactionFileProcessingState>
    {
        public ReactionFileProcessingStateMachine()
        {
            InstanceState(x => x.CurrentState);

            Event(() => ProcessFile, x => x.CorrelateById(context => context.Message.Id).SelectId(context => context.Message.Id));
            Event(() => StatusChanged, x => x.CorrelateById(context => context.Message.Id));
            Event(() => TotalRecordsUpdated, x => x.CorrelateById(context => context.Message.Id));
            Event(() => FieldsAdded, x => x.CorrelateById(context => context.Message.Id));
            Event(() => FileParsed, x => x.CorrelateById(context => context.Message.CorrelationId));
            Event(() => FileParseFailed, x => x.CorrelateById(context => context.Message.CorrelationId));
            Event(() => ImageGenerated, x => x.CorrelateById(context => context.Message.CorrelationId));
            Event(() => ImageGenerationFailed, x => x.CorrelateById(context => context.Message.CorrelationId));
            Event(() => ReactionProcessed, x => x.CorrelateById(context => context.Message.CorrelationId));
	        Event(() => NodeStatusPersisted, x => x.CorrelateById(context => context.Message.Id));
	        Event(() => StatusPersisted, x => x.CorrelateById(context => context.Message.Id));
            Event(() => MetadataGenerated, x => x.CorrelateById(context => context.Message.Id));

            CompositeEvent(() => EndProcessing, x => x.AllProcessed, FileParseDone, ImageGenerationDone, AllRecordsProcessingDone);
            CompositeEvent(() => FileParseDone, x => x.AllParsingStepsDone, TotalRecordsUpdated, FieldsAdded);
            CompositeEvent(() => AllPersisted, x => x.AllPersisted, StatusChanged, StatusPersistenceDone, NodeStatusPersistenceDone);

            Initially(
                When(ProcessFile)
                    .TransitionTo(Processing)
                    .ThenAsync(async context =>
                    {
                        context.Instance.Created = DateTimeOffset.UtcNow;
                        context.Instance.Updated = DateTimeOffset.UtcNow;
                        context.Instance.FileId = context.Data.Id;
                        context.Instance.UserId = context.Data.UserId;
                        context.Instance.BlobId = context.Data.BlobId;
                        context.Instance.Bucket = context.Data.Bucket;
                        context.Instance.FileStatus = FileStatus.Loaded;

                        await context.Raise(BeginProcessing);
                    })
                );

            During(Processing,
                Ignore(NodeStatusPersisted),
                Ignore(StatusPersisted),
                When(BeginProcessing)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<ChangeStatus>(new
                        {
                            Id = context.Instance.FileId,
                            Status = FileStatus.Processing,
                            UserId = context.Instance.UserId
                        });
                    }),
                When(StatusChanged)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<ParseFile>(new
                        {
                            Id = context.Instance.FileId,
                            BlobId = context.Instance.BlobId,
                            Bucket = context.Instance.Bucket,
                            UserId = context.Instance.UserId,
                            CorrelationId = context.Instance.CorrelationId
                        });
                    }),
                When(FileParsed)
                    .ThenAsync(async context =>
                    {
                        if (context.Data.TimeStamp > context.Instance.Updated)
                            context.Instance.Updated = context.Data.TimeStamp;

                        context.Instance.TotalParsedRecords = context.Data.TotalRecords;
                        context.Instance.Fields = context.Data.Fields;

                        await context.CreateConsumeContext().Publish<UpdateTotalRecords>(new
                        {
                            Id = context.Instance.FileId,
                            UserId = context.Instance.UserId,
                            TotalRecords = context.Instance.TotalParsedRecords
                        });

                        await context.CreateConsumeContext().Publish<AddFields>(new
                        {
                            Id = context.Instance.FileId,
                            context.Instance.UserId,
                            context.Instance.Fields
                        });

                        context.Instance.FileStatus = FileStatus.Parsed;
                    }),
                When(FileParseFailed)
                    .TransitionTo(Failed)
                    .ThenAsync(async context =>
                    {
                        if (context.Data.TimeStamp > context.Instance.Updated)
                            context.Instance.Updated = context.Data.TimeStamp;

                        await context.Raise(ProcessingFailed);
                    }),
                When(ImageGenerated)
                    .ThenAsync(async context => {
                        if (context.Data.TimeStamp > context.Instance.Updated)
                            context.Instance.Updated = context.Data.TimeStamp;

                        context.Instance.Image = context.Data.Image;

                        await context.CreateConsumeContext().Publish<AddImage>(new
                        {
                            Id = context.Instance.FileId,
                            UserId = context.Instance.UserId,
                            Image = new Image(context.Instance.Bucket, context.Data.Image.Id, context.Data.Image.Format, context.Data.Image.MimeType, context.Data.Image.Width, context.Data.Image.Height, context.Data.Image.Exception)
                        });

                        await context.Raise(ImageGenerationDone);
                    }),
                When(ReactionProcessed)
                    .ThenAsync(async context => {
                        if (context.Data.TimeStamp > context.Instance.Updated)
                            context.Instance.Updated = context.Data.TimeStamp;

                        context.Instance.ProcessedRecords++;

                        if (context.Instance.TotalParsedRecords == context.Instance.ProcessedRecords + context.Instance.FailedRecords)
                        {
                            await context.Raise(AllRecordsProcessingDone);
                        }
                    }),
                When(EndProcessing)
                    .TransitionTo(PostProcessing)
                    .ThenAsync(async context =>
                    {
                        await context.Raise(BeginPostProcessing);
                    })
                );

            During(PostProcessing,
                Ignore(NodeStatusPersisted),
                Ignore(StatusPersisted),
                When(BeginPostProcessing)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<GenerateMetadata>(new
                        {
                            context.Instance.FileId
                        });
                    }),
                When(MetadataGenerated)
                    .ThenAsync(async context =>
                    {
                        await context.Raise(EndPostProcessing);
                    }),
                When(EndPostProcessing)
                    .TransitionTo(Processed)
                    .ThenAsync(async context =>
                    {
                        await context.Raise(BeginProcessed);
                    })
            );

            During(Processed,
                When(BeginProcessed)
                    .ThenAsync(async context =>
                    {
                        FileStatus status = FileStatus.Processed;

                        if (context.Instance.ProcessedRecords == 0)
                        {
                            status = FileStatus.Failed;
                        }

                        await context.CreateConsumeContext().Publish<ChangeStatus>(new
                        {
                            Id = context.Instance.FileId,
                            UserId = context.Instance.UserId,
                            Status = status
                        });
                    }),
				When(NodeStatusPersisted) 
					.ThenAsync(async context =>
					{
						await context.Raise(NodeStatusPersistenceDone);
					}),
				When(StatusPersisted) 
					.ThenAsync(async context =>
					{
						await context.Raise(StatusPersistenceDone);
					}),
		        When(AllPersisted)
			        .ThenAsync(async context =>
			        {
				        await context.Raise(EndProcessed);
			        }),
                When(EndProcessed)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<ReactionFileProcessed>(new
                        {
                            Id = context.Instance.FileId,
                            BlobId = context.Instance.BlobId,
                            Bucket = context.Instance.Bucket,
                            FailedRecords = context.Instance.FailedRecords,
                            ProcessedRecords = context.Instance.ProcessedRecords,
                            CorrelationId = context.Instance.CorrelationId,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    })
                    .Finalize()
                );

            During(Failed,
                Ignore(NodeStatusPersisted),
                Ignore(StatusPersisted),
                When(ProcessingFailed)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<ChangeStatus>(new
                        {
                            Id = context.Instance.FileId,
                            UserId = context.Instance.UserId,
                            Status = FileStatus.Failed
                        });
                    }),
                When(StatusChanged)
                    .ThenAsync(async context =>
                    {
                        await context.CreateConsumeContext().Publish<ReactionFileProcessed>(new
                        {
                            Id = context.Instance.FileId,
                            BlobId = context.Instance.BlobId,
                            Bucket = context.Instance.Bucket,
                            FailedRecords = context.Instance.FailedRecords,
                            ProcessedRecords = context.Instance.ProcessedRecords,
                            CorrelationId = context.Instance.CorrelationId,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    })
                    .Finalize()
                );

            SetCompletedWhenFinalized();
        }

        public Event<MetadataGenerated> MetadataGenerated { get; private set; }
        public Event<ProcessReactionFile> ProcessFile { get; private set; }
        public Event<StatusChanged> StatusChanged { get; private set; }
        public Event<FileParsed> FileParsed { get; private set; }
        public Event<FileParseFailed> FileParseFailed { get; private set; }
        public Event<ImageGenerated> ImageGenerated { get; private set; }
        public Event<ImageGenerationFailed> ImageGenerationFailed { get; private set; }
        public Event<ReactionProcessed> ReactionProcessed { get; private set; }
        public Event<TotalRecordsUpdated> TotalRecordsUpdated { get; private set; }
        public Event<FieldsAdded> FieldsAdded { get; private set; }
        public Event<NodeStatusPersisted> NodeStatusPersisted { get; private set; }
        public Event<StatusPersisted> StatusPersisted { get; private set; }

        State Processing { get; set; }
        Event BeginProcessing { get; set; }
        Event EndProcessing { get; set; }

        State PostProcessing { get; set; }
        Event BeginPostProcessing { get; set; }
        Event EndPostProcessing { get; set; }

        State Processed { get; set; }
        Event BeginProcessed { get; set; }
        Event EndProcessed { get; set; }

        State Failed { get; set; }
        
        Event FileParseDone { get; set; }
        Event ImageGenerationDone { get; set; }
        Event AllRecordsProcessingDone { get; set; }
        Event ProcessingFailed { get; set; }
        
	    Event AllPersisted { get; set; }
	    Event NodeStatusPersistenceDone { get; set; }
	    Event StatusPersistenceDone { get; set; }
    }
}
