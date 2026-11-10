import React, { useCallback, useState } from 'react';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import MovieEdition from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

export interface PrimarySelection {
  target: MovieEdition;
  expectedPrimaryEditionId: number;
}

interface SelectPrimaryEditionModalProps {
  movieId: number;
  selection?: PrimarySelection;
  onSelected(): void;
  onError(message: string): void;
  onClose(): void;
}

function SelectPrimaryEditionModal({
  movieId,
  selection,
  onSelected,
  onError,
  onClose,
}: SelectPrimaryEditionModalProps) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const onCancel = useCallback(() => {
    if (!isSubmitting) {
      onClose();
    }
  }, [isSubmitting, onClose]);
  const onConfirm = useCallback(async () => {
    if (isSubmitting || !selection) {
      return;
    }
    setIsSubmitting(true);
    try {
      await postEdition<MovieEdition>(
        `/movieedition/${selection.target.id}/primary`,
        {
          movieId,
          expectedPrimaryEditionId: selection.expectedPrimaryEditionId,
          expectedMovieFileId: selection.target.movieFileId ?? null,
          expectedQualityProfileId: selection.target.qualityProfileId,
        }
      );
      onSelected();
    } catch (requestError) {
      onError(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, translate('PrimaryEditionFailed')) ||
              translate('PrimaryEditionFailed')
          : translate('PrimaryEditionFailed')
      );
    } finally {
      setIsSubmitting(false);
    }
  }, [isSubmitting, selection, movieId, onSelected, onError]);

  return (
    <ConfirmModal
      isOpen={!!selection}
      title={translate('SelectPrimaryEdition')}
      isSpinning={isSubmitting}
      confirmLabel={translate('SelectPrimaryEdition')}
      cancelLabel={translate('Cancel')}
      message={translate('PrimaryEditionConfirm', {
        edition: selection?.target.name ?? '',
      })}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}

export default SelectPrimaryEditionModal;
