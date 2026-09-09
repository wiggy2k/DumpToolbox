using Microsoft.Data.Sqlite;

namespace DumpToolbox.Core;

public sealed partial class DiscEvidenceService
{
    private static async Task StoreUdfEvidenceAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long imageId,
        UdfStructureEvidence evidence,
        CancellationToken cancellationToken)
    {
        foreach (UdfDescriptorStructureEvidence descriptor in evidence.Descriptors)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO udf_descriptors(image_id,sequence_name,sequence_index,lba,tag_id,tag_version,tag_checksum,
 tag_checksum_valid,tag_serial,descriptor_crc,descriptor_crc_length,descriptor_crc_valid,tag_location,
 reserved_nonzero_bytes,implementation_identifier,payload,payload_sha1,details)
VALUES($image,$sequence,$index,$lba,$tag,$version,$checksum,$checksumValid,$serial,$crc,$crcLength,
 $crcValid,$location,$reserved,$implementation,$payload,$sha1,$details);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$sequence", descriptor.Sequence);
            command.Parameters.AddWithValue("$index", descriptor.SequenceIndex);
            command.Parameters.AddWithValue("$lba", descriptor.Lba);
            command.Parameters.AddWithValue("$tag", (int)descriptor.TagId);
            command.Parameters.AddWithValue("$version", (int)descriptor.TagVersion);
            command.Parameters.AddWithValue("$checksum", (int)descriptor.TagChecksum);
            command.Parameters.AddWithValue("$checksumValid", descriptor.TagChecksumValid ? 1 : 0);
            command.Parameters.AddWithValue("$serial", (int)descriptor.TagSerial);
            command.Parameters.AddWithValue("$crc", (int)descriptor.DescriptorCrc);
            command.Parameters.AddWithValue("$crcLength", (int)descriptor.DescriptorCrcLength);
            command.Parameters.AddWithValue("$crcValid", descriptor.DescriptorCrcValid ? 1 : 0);
            command.Parameters.AddWithValue("$location", (long)descriptor.TagLocation);
            command.Parameters.AddWithValue("$reserved", descriptor.ReservedNonZeroBytes);
            command.Parameters.AddWithValue("$implementation", descriptor.ImplementationIdentifier);
            command.Parameters.AddWithValue("$payload", descriptor.Payload);
            command.Parameters.AddWithValue("$sha1", descriptor.PayloadSha1);
            command.Parameters.AddWithValue("$details", descriptor.Details);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (UdfPartitionMapStructureEvidence map in evidence.PartitionMaps)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO udf_partition_maps(image_id,lvd_lba,map_index,map_type,map_length,identifier,
 volume_sequence_number,partition_number,reserved_nonzero_bytes,payload,payload_sha1)
VALUES($image,$lba,$index,$type,$length,$identifier,$volume,$partition,$reserved,$payload,$sha1);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$lba", map.LogicalVolumeDescriptorLba);
            command.Parameters.AddWithValue("$index", map.MapIndex);
            command.Parameters.AddWithValue("$type", (int)map.MapType);
            command.Parameters.AddWithValue("$length", map.MapLength);
            command.Parameters.AddWithValue("$identifier", map.Identifier);
            command.Parameters.AddWithValue("$volume", (int)map.VolumeSequenceNumber);
            command.Parameters.AddWithValue("$partition", (int)map.PartitionNumber);
            command.Parameters.AddWithValue("$reserved", map.ReservedNonZeroBytes);
            command.Parameters.AddWithValue("$payload", map.Payload);
            command.Parameters.AddWithValue("$sha1", map.PayloadSha1);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (UdfVatStructureEvidence vat in evidence.Vats)
        {
            using SqliteCommand command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO udf_vats(image_id,generation,is_latest,lba,partition_block,tag_id,file_type,tag_serial,
 tag_checksum_valid,descriptor_crc_valid,allocation_type,information_length,header_length,
 implementation_use_length,implementation_identifier,implementation_use,previous_vat_icb_location,file_count,directory_count,
 minimum_read_revision,minimum_write_revision,maximum_write_revision,entry_count,mapped_entry_count,
 unused_entry_count,out_of_range_entry_count,reserved_nonzero_bytes,payload,payload_sha1)
VALUES($image,$generation,$latest,$lba,$block,$tag,$fileType,$serial,$checksumValid,$crcValid,$allocation,
 $informationLength,$headerLength,$implementationLength,$implementation,$implementationUse,$previous,$files,$directories,
 $minRead,$minWrite,$maxWrite,$entries,$mapped,$unused,$outOfRange,$reserved,$payload,$sha1);";
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$generation", vat.Generation);
            command.Parameters.AddWithValue("$latest", vat.IsLatest ? 1 : 0);
            command.Parameters.AddWithValue("$lba", vat.Lba);
            command.Parameters.AddWithValue("$block", vat.PartitionBlock);
            command.Parameters.AddWithValue("$tag", (int)vat.TagId);
            command.Parameters.AddWithValue("$fileType", (int)vat.FileType);
            command.Parameters.AddWithValue("$serial", (int)vat.TagSerial);
            command.Parameters.AddWithValue("$checksumValid", vat.TagChecksumValid ? 1 : 0);
            command.Parameters.AddWithValue("$crcValid", vat.DescriptorCrcValid ? 1 : 0);
            command.Parameters.AddWithValue("$allocation", vat.AllocationType);
            command.Parameters.AddWithValue("$informationLength", vat.InformationLength);
            command.Parameters.AddWithValue("$headerLength", vat.HeaderLength);
            command.Parameters.AddWithValue("$implementationLength", vat.ImplementationUseLength);
            command.Parameters.AddWithValue("$implementation", vat.ImplementationIdentifier);
            command.Parameters.AddWithValue("$implementationUse", vat.ImplementationUse);
            command.Parameters.AddWithValue("$previous", (long)vat.PreviousVatIcbLocation);
            command.Parameters.AddWithValue("$files", (long)vat.FileCount);
            command.Parameters.AddWithValue("$directories", (long)vat.DirectoryCount);
            command.Parameters.AddWithValue("$minRead", (int)vat.MinimumReadRevision);
            command.Parameters.AddWithValue("$minWrite", (int)vat.MinimumWriteRevision);
            command.Parameters.AddWithValue("$maxWrite", (int)vat.MaximumWriteRevision);
            command.Parameters.AddWithValue("$entries", vat.EntryCount);
            command.Parameters.AddWithValue("$mapped", vat.MappedEntryCount);
            command.Parameters.AddWithValue("$unused", vat.UnusedEntryCount);
            command.Parameters.AddWithValue("$outOfRange", vat.OutOfRangeEntryCount);
            command.Parameters.AddWithValue("$reserved", vat.ReservedNonZeroBytes);
            command.Parameters.AddWithValue("$payload", vat.Payload);
            command.Parameters.AddWithValue("$sha1", vat.PayloadSha1);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
