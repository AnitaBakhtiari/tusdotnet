﻿using System.Net;
using System.Threading;
using System.Threading.Tasks;
using tusdotnet.Models;

namespace tusdotnet.Tus2
{
    public abstract class TusBaseControllerEntryPoints
    {
        public virtual IMetadataParser MetadataParser { get; set; }

        public virtual bool AllowClientToDeleteFile { get; set; }

        public virtual EndpointContext TusContext { get; set; }

        internal OngoingUploadTransferServiceDiskBased OngoingUploadTransferService { get; set; }

        internal async Task<UploadRetrievingProcedureResponse> RetrieveOffsetEntryPoint()
        {
            /*
           If an upload is interrupted, the client MAY attempt to fetch the offset of the incomplete upload by sending a HEAD request to the server with the same Upload-Token header field ({{upload-token}}). The client MUST NOT initiate this procedure without the knowledge of server support.

           The request MUST use the HEAD method and include the Upload-Token header. The request MUST NOT include the Upload-Offset header or the Upload-Incomplete header. The server MUST reject the request with the Upload-Offset header or the Upload-Incomplete header by sending a 400 (Bad Request) response.

           The client MUST NOT perform the Offset Retrieving Procedure ({{offset-retrieving}}) while the Upload Transfer Procedures ({{upload-transfer}}) is in progress.

           If the server has resources allocated for this token, it MUST send back a 204 (No Content) response with a header Upload-Offset which indicates the resumption offset for the client.

           The offset MUST be accepted by a subsequent Upload Transfer Procedure ({{upload-transfer}}). Due to network delay and reordering, the server might still be receiving data from an ongoing transfer for the same token, which in the client perspective has failed. The server MAY terminate any transfers for the same token before sending the response by abruptly terminating the HTTP connection or stream. Alternatively, the server MAY keep the ongoing transfer alive but ignore further bytes received past the offset.

           The response SHOULD include Cache-Control: no-store header to prevent HTTP caching.

           If the server has no record of this token, it MUST respond with 404 (Not Found) status code.

           The client MUST NOT start more than one Upload Transfer Procedures ({{upload-transfer}}) based on the resumption offset from a single Offset Retrieving Procedure ({{offset-retrieving}}).
            * */

            try
            {
                var (store, headers, _) = TusContext;

                Tus2Validator.AssertNoInvalidHeaders(headers);
                await Tus2Validator.AssertFileExist(store, headers.UploadToken);

                await OngoingUploadTransferService.CancelOngoingUploads(headers.UploadToken);

                return await RetrieveOffset();

            }
            catch (Tus2AssertRequestException ex)
            {
                return new()
                {
                    Status = ex.Status,
                    ErrorMessage = ex.ErrorMessage
                };
            }
        }
        internal async Task<UploadCancellationProcedureResponse> DeleteEntryPoint()
        {
            /*
            If the client wants to terminate the transfer without the ability to resume, it MAY send a DELETE request to the server along with the Upload-Token which is an indication that the client is no longer interested in uploading this body and the server can release resources associated with this token. The client MUST NOT initiate this procedure without the knowledge of server support.

            The request MUST use the DELETE method and include the Upload-Token header. The request MUST NOT include the Upload-Offset header or the Upload-Incomplete header. The server MUST reject the request with the Upload-Offset header or the Upload-Incomplete header by sending a 400 (Bad Request) response.

            If the server has successfully released the resources allocated for this token, it MUST send back a 204 (No Content) response.

            The server MAY terminate any ongoing Upload Transfer Procedure ({{upload-transfer}}) for the same token before sending the response by abruptly terminating the HTTP connection or stream.

            If the server has no record of the token in Upload-Token, it MUST respond with 404 (Not Found) status code.

            If the server does not support cancellation, it MUST respond with 405 (Method Not Allowed) status code.
             * */
            try
            {
                if (!AllowClientToDeleteFile)
                {
                    return new UploadCancellationProcedureResponse
                    {
                        Status = HttpStatusCode.MethodNotAllowed
                    };
                }

                var (store, headers, _) = TusContext;

                Tus2Validator.AssertNoInvalidHeaders(headers);
                await Tus2Validator.AssertFileExist(store, headers.UploadToken);

                await OngoingUploadTransferService.CancelOngoingUploads(headers.UploadToken);

                return await Delete();

            }
            catch (Tus2AssertRequestException ex)
            {
                return new()
                {
                    Status = ex.Status,
                    ErrorMessage = ex.ErrorMessage
                };
            }
        }

        internal async Task<UploadTransferProcedureResponse> WriteDataEntryPoint()
        {
            /*
            The Upload Transfer Procedure can be used for either starting a new upload, or resuming an existing upload. A limited form of this procedure MAY be used by the client to start a new upload without the knowledge of server support.

             This procedure is designed to be compatible with a regular upload. Therefore all methods are allowed with the exception of GET, HEAD, DELETE, and OPTIONS. And all response status codes are allowed. The client is RECOMMENDED to use POST request if not otherwise specified.

             The client MUST use the same method throughout an entire upload. The server SHOULD reject the attempt to resume an upload with a different method with 400 (Bad Request) response.

             The client MUST NOT perform multiple Upload Transfer Procedures ({{upload-transfer}}) for the same file in parallel.

             The request MUST include the Upload-Token header field ({{upload-token}}) which uniquely identifies an upload. The client MUST NOT reuse the token for a different upload.

             When resuming an upload, the Upload-Offset header field ({{upload-offset}}) MUST be set to the resumption offset. The resumption offset 0 indicates a new upload. The absence of the Upload-Offset header field implies the resumption offset of 0.

             If the end of the request body is not the end of the upload, the Upload-Incomplete header field ({{upload-incomplete}}) MUST be set to true.

             The client MAY send the metadata of the file using headers such as Content-Type (see {{Section 8.3 of HTTP}} and Content-Disposition {{!RFC6266}} when starting a new upload. It is OPTIONAL for the client to repeat the metadata when resuming an upload.

             If the server has no record of the token but the offset is non-zero, it MUST respond with 404 (Not Found) status code.

             The server MAY terminate any ongoing Upload Transfer Procedure ({{upload-transfer}}) for the same token. Since the client is not allowed to perform multiple transfers in parallel, the server can assume that the previous attempt has already failed. Therefore, the server MAY abruptly terminate the previous HTTP connection or stream.

             If the offset in the Upload-Offset header field does not match the value 0, the offset provided by the immediate previous Offset Retrieving Procedure ({{offset-retrieving}}), or the end offset of the immediate previous incomplete transfer, the server MUST respond with 400 (Bad Request) status code.

             If the request completes successfully and the entire file is received, the server MUST acknowledge it by responding with a successful status code between 200 and 299 (inclusive). Server is RECOMMENDED to use 201 (Created) response if not otherwise specified. The response MUST NOT include the Upload-Incomplete header.

             If the request completes successfully but the file is not complete yet indicated by the Upload-Incomplete header, the server MUST acknowledge it by responding with the 201 (Created) status code with the Upload-Incomplete header set to true.
              * */

            try
            {
                var (store, headers, httpContext) = TusContext;

                var metadata = MetadataParser?.Parse(httpContext);

                var uploadOffset = headers.UploadOffset ?? 0;

                var fileExist = await Tus2Validator.AssertFileExist(store, headers.UploadToken, uploadOffset != 0);

                if (!fileExist)
                {
                    var createFileResponse = await CreateFile(new() { Metadata = metadata });
                    if (createFileResponse.IsError)
                    {
                        return new()
                        {
                            Status = createFileResponse.Status,
                            ErrorMessage = createFileResponse.ErrorMessage
                        };
                    }
                }
                else
                {
                    var fileIsComplete = await store.IsComplete(headers.UploadToken);
                    if (fileIsComplete)
                    {
                        return new()
                        {
                            Status = HttpStatusCode.BadRequest,
                            ErrorMessage = "File is already completed"
                        };
                    }
                }

                await OngoingUploadTransferService.CancelOngoingUploads(headers.UploadToken);

                var ongoingCancellationToken = await OngoingUploadTransferService.StartOngoing(headers.UploadToken);
                await using var finishOngoing = Deferrer.Defer(() => OngoingUploadTransferService.FinishOngoing(headers.UploadToken));

                // TODO: "before processing the request body" seems a bit strange here
                // "The server MUST terminate any ongoing Upload Transfer Procedure for the same token before processing the request body."
                // as we will end up with mismatches in upload-offset?
                // Should we cancel ongoing uploads directly or just before processing the body?

                await Tus2Validator.AssertValidOffset(store, headers.UploadToken, uploadOffset);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, ongoingCancellationToken);
                var guardedPipeReader = new ClientDisconnectGuardedPipeReader(httpContext.Request.BodyReader, cts.Token);

                var writeDataContext = new WriteDataContext()
                {
                    BodyReader = guardedPipeReader,
                    CancellationToken = cts.Token,
                    Metadata = metadata
                };

                return await WriteData(writeDataContext);
            }
            catch (Tus2AssertRequestException exc)
            {
                return new()
                {
                    Status = exc.Status,
                    ErrorMessage = exc.ErrorMessage
                };
            }
        }

        public abstract Task<UploadRetrievingProcedureResponse> RetrieveOffset();

        public abstract Task<CreateFileProcedureResponse> CreateFile(CreateFileContext createFileContext);

        public abstract Task<UploadTransferProcedureResponse> WriteData(WriteDataContext writeDataContext);

        public abstract Task<UploadCancellationProcedureResponse> Delete();

        public abstract Task FileComplete();
    }
}
